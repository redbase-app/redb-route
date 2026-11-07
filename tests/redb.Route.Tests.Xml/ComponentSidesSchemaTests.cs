using System.Xml.Linq;
using AwesomeAssertions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// A component with one side (<c>[EndpointRole]</c> on its class) carries it in the catalog, and the editor schema
/// offers the scheme only on that side: no <c>&lt;from&gt;&lt;log/&gt;&lt;/from&gt;</c>, no
/// <c>&lt;to&gt;&lt;timer/&gt;&lt;/to&gt;</c>.
/// </summary>
public sealed class ComponentSidesSchemaTests
{
    private static readonly IReadOnlyList<CatalogComponent> Catalog =
        ComponentCatalog.Build([new LogComponent(), new TimerComponent(), new DirectComponent()]);

    private static IReadOnlyList<(int Line, int Column, string Message)> Validate(string from, string to)
        => XmlRouteSchema.Validate(XDocument.Parse($"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="r">
                <from>{from}</from>
                <to>{to}</to>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), ElementRegistry.CreateDefault(), Catalog);

    [Fact]
    public void Catalog_carries_the_only_side_and_nothing_for_both()
    {
        Catalog.Single(c => c.Scheme == "log").Role.Should().Be(EndpointRole.Producer);
        Catalog.Single(c => c.Scheme == "timer").Role.Should().Be(EndpointRole.Consumer);
        Catalog.Single(c => c.Scheme == "direct").Role.Should().BeNull("absent means both sides");

        ComponentCatalog.ToJson(ComponentCatalog.Build([new TimerComponent()])).Should().Contain("\"role\": \"consumer\"");
        ComponentCatalog.ToJson(ComponentCatalog.Build([new DirectComponent()])).Should().NotContain("\"role\"");
    }

    [Fact]
    public void Schema_has_no_type_for_the_missing_side()
    {
        var schema = XmlRouteSchema.Generate(ElementRegistry.CreateDefault(), Catalog).ToString();

        schema.Should().Contain("name=\"logProducer\"").And.NotContain("name=\"logConsumer\"");
        schema.Should().Contain("name=\"timerConsumer\"").And.NotContain("name=\"timerProducer\"");
        schema.Should().Contain("name=\"directProducer\"").And.Contain("name=\"directConsumer\"");
    }

    [Fact]
    public void Schema_refuses_a_producer_only_scheme_in_from()
        => Validate("""<log path="x"/>""", """<direct path="out"/>""")
            .Should().Contain(p => p.Line == 3 && p.Message.Contains("log"));

    [Fact]
    public void Schema_refuses_a_consumer_only_scheme_in_to()
        => Validate("""<timer path="t"/>""", """<timer path="t"/>""")
            .Should().ContainSingle().Which.Line.Should().Be(4);

    [Fact]
    public void Schema_takes_each_scheme_on_its_own_side()
        => Validate("""<timer path="t"/>""", """<log path="x"/>""").Should().BeEmpty();
}
