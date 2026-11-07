using redb.Route.Core;
using redb.Route.JsonTransform;
using redb.Route.TestKit;

namespace redb.Route.Tests.JsonTransform;

/// <summary>Code review 2026-09-01, fourth batch: stream input, error naming, content type of the result.</summary>
public class JsonTransformReviewTests
{
    [Fact]
    public async Task StreamBody_IsReadFromItsPosition_AndLeftThere()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://stream").TransformJson(TextSource.Inline("orderId"), JsonTransformOutput.String).To("mock://stream"));
        await ctx.Start();

        var stream = new MemoryStream("""{"orderId": 7}"""u8.ToArray());
        await ctx.SendBody("direct://stream", stream);

        ctx.Mock("mock://stream").ReceivedExchanges[0].In.Body.Should().BeOfType<string>().Which.Should().Be("7");
        stream.CanRead.Should().BeTrue("the stream belongs to the exchange, the step must not close it");
        stream.Position.Should().Be(0, "a later step may need to read the stream too");
    }

    [Fact]
    public async Task ANonJsonBody_FailsWithTheSpecificationName()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://bad").TransformJson(TextSource.Inline("orderId")));
        await ctx.Start();

        var act = () => ctx.SendBody("direct://bad", "not json at all");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*<inline>*not JSON*");
    }

    [Fact]
    public async Task NodeOutput_CarriesTheJsonContentType()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://node")
                .Process(e => e.In.ContentType = "text/plain")
                .TransformJson(TextSource.Inline("$"), JsonTransformOutput.Node)
                .To("mock://node"));
        await ctx.Start();

        await ctx.SendBody("direct://node", """{"a":1}""");

        ctx.Mock("mock://node").ReceivedExchanges[0].In.ContentType.Should().Be("application/json");
    }
}
