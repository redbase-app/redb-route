using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Core;
using redb.Route.JsonTransform;
using redb.Route.TestKit;
using static redb.Route.Core.TextSource;

namespace redb.Route.Tests.JsonTransform;

public class JsonTransformTests
{
    private const string Order = """
        {"orderId": 7, "address": {"city": "Kazan", "zip": "420000"}, "items": [{"sku": "a", "qty": 2, "price": 1.5}, {"sku": "b", "qty": 1, "price": 10}]}
        """;

    private static async Task<object?> Run(TextSource spec, object? body, JsonTransformOutput output = JsonTransformOutput.String,
        IDictionary<string, object?>? headers = null, Action<JsonTransformOptions>? options = null)
    {
        await using var ctx = new RouteContext();
        if (options is not null) ctx.UseJsonTransform(options);
        ctx.AddRoutes(b => b.From("direct://in").TransformJson(spec, output).To("mock://out"));
        await ctx.Start();
        await ctx.SendBodyAndHeaders("direct://in", body, headers ?? new Dictionary<string, object?>());
        return ctx.Mock("mock://out").ReceivedExchanges[0].In.Body;
    }

    private static JsonElement Json(object? body) => JsonDocument.Parse((string)body!).RootElement;

    [Fact]
    public async Task Rename_Nest_UnwrapArray_Default_FromFile()
    {
        var result = Json(await Run(File("Transforms/order-to-shipment.jsonata"), Order));

        result.GetProperty("id").GetInt32().Should().Be(7);
        result.GetProperty("to").GetProperty("city").GetString().Should().Be("Kazan");
        result.GetProperty("skus").EnumerateArray().Select(e => e.GetString()).Should().Equal("a", "b");
        result.GetProperty("total").GetDecimal().Should().Be(13m);
        result.GetProperty("status").GetString().Should().Be("new", "the default applies when the field is missing");
    }

    [Fact]
    public async Task Output_IsJsonText_WithContentType()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://ct").TransformJson(Inline("{ \"id\": orderId }")).To("mock://ct"));
        await ctx.Start();

        await ctx.SendBody("direct://ct", Order);

        var message = ctx.Mock("mock://ct").ReceivedExchanges[0].In;
        message.Body.Should().Be("""{"id":7}""");
        message.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task HeadersAndProperties_AreBound()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://bind")
                .SetProperty("region", "eu")
                .TransformJson(Inline("{ \"tenant\": $headers.tenant, \"region\": $properties.region, \"id\": orderId }"))
                .To("mock://bind"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://bind", Order, "tenant", "acme");

        var result = Json(ctx.Mock("mock://bind").ReceivedExchanges[0].In.Body);
        result.GetProperty("tenant").GetString().Should().Be("acme");
        result.GetProperty("region").GetString().Should().Be("eu");
        result.GetProperty("id").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task Input_Poco_Bytes_And_JsonNode()
    {
        var spec = Inline("{ \"city\": address.city }");

        Json(await Run(spec, new { address = new { city = "Ufa" } })).GetProperty("city").GetString().Should().Be("Ufa");
        Json(await Run(spec, "{\"address\":{\"city\":\"Perm\"}}"u8.ToArray())).GetProperty("city").GetString().Should().Be("Perm");
        Json(await Run(spec, JsonNode.Parse("{\"address\":{\"city\":\"Omsk\"}}"))).GetProperty("city").GetString().Should().Be("Omsk");
    }

    [Fact]
    public async Task NodeOutput_LeavesAJsonNodeBody()
    {
        var body = await Run(Inline("{ \"id\": orderId }"), Order, JsonTransformOutput.Node);

        body.Should().BeAssignableTo<JsonNode>().Which["id"]!.GetValue<long>().Should().Be(7, "JSONata integers arrive as Int64");
    }

    [Fact]
    public async Task UndefinedResult_IsNullBody()
    {
        (await Run(Inline("nothing.here"), Order)).Should().BeNull();
    }

    [Fact]
    public async Task Indent_Option()
    {
        var body = (string)(await Run(Inline("{ \"id\": orderId }"), Order, options: o => o.Indent = true))!;

        body.Should().Contain("\n");
        Json(body).GetProperty("id").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task BadSpecification_FailsStart_WithName()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://bad").TransformJson(Inline("{ \"id\": ")));

        var act = () => ctx.Start();

        var ex = await act.Should().ThrowAsync<JsonTransformCompilationException>();
        ex.Which.SpecificationName.Should().Be("<inline>");
        ex.Which.Message.Should().StartWith("JSON transform '<inline>':");
    }

    [Fact]
    public async Task MissingFile_FailsStart_WithLocator()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://nf").TransformJson("Transforms/nope.jsonata"));

        var act = () => ctx.Start();

        (await act.Should().ThrowAsync<JsonTransformCompilationException>()).Which.Message.Should().Contain("Transforms/nope.jsonata").And.Contain("not found");
    }

    [Fact]
    public async Task InvalidJsonBody_FailsTheMessage_NotStart()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://badbody").TransformJson(Inline("{ \"id\": orderId }")));
        await ctx.Start();

        var act = () => ctx.SendBody("direct://badbody", "not json");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public void Cache_SameSource_CompilesOnce()
    {
        var options = new JsonTransformOptions();

        var first = JsonTransformEngine.Compile(File("Transforms/order-to-shipment.jsonata"), options);
        var second = JsonTransformEngine.Compile(File("Transforms/order-to-shipment.jsonata"), options);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task AbsolutePath_And_BaseDirectoryOption()
    {
        var dir = Directory.CreateTempSubdirectory("redb-jsonata-");
        try
        {
            var path = Path.Combine(dir.FullName, "t.jsonata");
            await System.IO.File.WriteAllTextAsync(path, "{ \"n\": $count(items) }");

            Json(await Run(File(path), Order)).GetProperty("n").GetInt32().Should().Be(2);
            Json(await Run(File("t.jsonata"), Order, options: o => o.BaseDirectory = dir.FullName)).GetProperty("n").GetInt32().Should().Be(2);
        }
        finally { dir.Delete(recursive: true); }
    }
}
