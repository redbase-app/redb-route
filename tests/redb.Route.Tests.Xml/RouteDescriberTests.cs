using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Diagnostics;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Route-XML Ф3.2: <see cref="RouteDescriber"/> — deterministic, human-readable text over a
/// definition tree, the one visitor shared by equivalence tests (here), the Ф6 generator
/// round-trip and Mermaid. The key promise: a route loaded from XML and the same route written
/// in post-V4 C# describe IDENTICALLY.
/// </summary>
public class RouteDescriberTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private const string Xml = """
        <routes xmlns="urn:redb:route:1.0">
          <route id="descr-route">
            <from uri="direct://descr-in"/>
            <setHeader name="priority" expr="${header.amount} > 1000 ? 'high' : 'normal'"/>
            <filter expr="header.amount > 0">
              <choice>
                <when expr="header.type == 'order'">
                  <to uri="direct://descr-orders"/>
                </when>
                <otherwise>
                  <to uri="direct://descr-dlq"/>
                </otherwise>
              </choice>
            </filter>
          </route>
        </routes>
        """;

    private static IReadOnlyList<IProcessorDefinition> Definitions(RouteContext context)
    {
        var definitions = new List<IProcessorDefinition>();
        foreach (var builder in context.RouteBuilders)
        {
            if (!builder.IsBuilt)
                builder.InternalBuild(context);
            definitions.AddRange(builder.Definitions);
        }
        return definitions;
    }

    [Fact]
    public void TwoLoadsOfTheSameXml_DescribeByteEqual()
    {
        _context.AddXmlRoutesFromContent(Xml);
        using var second = new RouteContext();
        second.AddXmlRoutesFromContent(Xml);

        var first = RouteDescriber.Describe(Definitions(_context));
        var again = RouteDescriber.Describe(Definitions(second));

        again.Should().Be(first);
        first.Should().NotBeEmpty();
    }

    [Fact]
    public void XmlRoute_AndItsPostV4CSharpTwin_DescribeIdentically()
    {
        _context.AddXmlRoutesFromContent(Xml);
        using var codeContext = new RouteContext();
        codeContext.AddRoutes(r => r.From("direct://descr-in")
            .RouteId("descr-route")
            .SetHeader("priority", RouteBuilder.Expr("${header.amount} > 1000 ? 'high' : 'normal'"))
            .Filter("header.amount > 0")
                .Choice()
                    .When("header.type == 'order'")
                        .To("direct://descr-orders")
                    .Otherwise()
                        .To("direct://descr-dlq")
                .EndChoice()
            .EndFilter());

        var fromXml = RouteDescriber.Describe(Definitions(_context));
        var fromCode = RouteDescriber.Describe(Definitions(codeContext));

        fromXml.Should().Be(fromCode);
    }

    [Fact]
    public void TheDescription_IsReadable_AndCarriesTheAuthoredText()
    {
        _context.AddXmlRoutesFromContent(Xml);

        var text = RouteDescriber.Describe(Definitions(_context));

        text.Should().Contain("header.amount > 0", "the filter shows the condition as written");
        text.Should().Contain("direct://descr-orders").And.Contain("direct://descr-dlq");
        text.Should().Contain("${header.amount} > 1000 ? 'high' : 'normal'");
        text.Should().NotContainAny("k__BackingField", "System.Func", "HashCode");
    }

    [Fact]
    public void DifferentExpressions_DescribeDifferently()
    {
        // Guards the failure mode where the describer skips so much that everything looks equal.
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="d1"><from uri="direct://d-in"/><filter expr="header.a > 1"><removeBody/></filter></route>
            </routes>
            """);
        using var other = new RouteContext();
        other.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="d1"><from uri="direct://d-in"/><filter expr="header.a > 2"><removeBody/></filter></route>
            </routes>
            """);

        RouteDescriber.Describe(Definitions(_context))
            .Should().NotBe(RouteDescriber.Describe(Definitions(other)));
    }

    [Fact]
    public void BranchChildren_CarryIdAndDescriptionToo()
    {
        // Р12: id/description are common to ALL elements — including the branch children
        // (<when>, <otherwise>, <catch>) that are parsed inside their parent's contribution.
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="branch-idd">
                <from uri="direct://branch-idd-in"/>
                <choice>
                  <when id="w-list" description="The list branch" expr="header.kind == 'list'">
                    <removeBody/>
                  </when>
                  <otherwise id="w-rest" description="Everything else">
                    <removeBody/>
                  </otherwise>
                </choice>
              </route>
            </routes>
            """);

        var text = RouteDescriber.Describe(Definitions(_context));

        text.Should().Contain("stepId=w-list").And.Contain("stepDescription=The list branch");
        text.Should().Contain("stepId=w-rest").And.Contain("stepDescription=Everything else");
    }

    [Fact]
    public void StepIdentity_FromXmlAttributes_IsPartOfTheDescription()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="idd">
                <from uri="direct://idd-in"/>
                <removeBody id="wipe" description="Clear the payload"/>
              </route>
            </routes>
            """);

        var text = RouteDescriber.Describe(Definitions(_context));

        text.Should().Contain("stepId=wipe").And.Contain("stepDescription=Clear the payload");
    }
}
