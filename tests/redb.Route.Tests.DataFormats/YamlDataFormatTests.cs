using redb.Route.Core;
using redb.Route.DataFormats.Yaml;
using redb.Route.TestKit;

namespace redb.Route.Tests.DataFormats;

public class YamlDataFormatTests
{
    private static readonly OrderRow Sample = new() { Id = 7, Customer = "acme", Amount = 12.5m };

    [Fact]
    public void RoundTrip_Poco_CamelCaseKeys()
    {
        var format = new YamlDataFormat();

        var bytes = format.Serialize(Sample);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        text.Should().Contain("id: 7").And.Contain("customer: acme").And.Contain("amount: 12.5");
        format.Deserialize<OrderRow>(bytes).Should().Be(Sample);
    }

    [Fact]
    public void DynamicTree_HasStringKeys()
    {
        var format = new YamlDataFormat();

        var tree = format.Deserialize<object>("""
            name: web
            replicas: 3
            ports:
              - 80
              - 443
            labels:
              tier: front
            """u8.ToArray());

        var map = tree.Should().BeOfType<Dictionary<string, object?>>().Subject;
        map["name"].Should().Be("web");
        map["ports"].Should().BeOfType<List<object?>>().Which.Should().Equal("80", "443");
        map["labels"].Should().BeOfType<Dictionary<string, object?>>().Which["tier"].Should().Be("front");
    }

    [Fact]
    public void BadYaml_FailsNamingTheFormat()
    {
        var act = () => new YamlDataFormat().Deserialize<OrderRow>("id: [unclosed"u8.ToArray());

        act.Should().Throw<InvalidOperationException>().WithMessage("YAML data format:*");
    }

    [Fact]
    public async Task Dsl_And_Registry_WithAliases()
    {
        await using var ctx = new RouteContext();
        ctx.AddYamlDataFormat();
        ctx.AddRoutes(b =>
        {
            b.From("direct://sugar").MarshalYaml().To("mock://wire").UnmarshalYaml<OrderRow>();
            b.From("direct://alias").Unmarshal<OrderRow>("text/yaml");
            b.From("direct://by-content-type").SetHeader("Content-Type", "application/x-yaml")
                .Process(e => e.In.ContentType = "application/x-yaml").Unmarshal<OrderRow>();
        });
        await ctx.Start();

        (await ctx.RequestBody<OrderRow>("direct://sugar", Sample)).Should().Be(Sample);
        (await ctx.RequestBody<OrderRow>("direct://alias", "id: 7\ncustomer: acme\namount: 12.5\n")).Should().Be(Sample);
        (await ctx.RequestBody<OrderRow>("direct://by-content-type", "id: 7\ncustomer: acme\namount: 12.5\n")).Should().Be(Sample);
        ctx.Mock("mock://wire").ReceivedExchanges[0].In.ContentType.Should().Be("application/yaml");
    }
}
