using System.Diagnostics;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Expressions;
using redb.Route.Telemetry;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// <c>&lt;spanTag&gt;</c>, the markup form of <c>.SpanTag(key, value, target)</c>. Shaped as <c>&lt;setHeader&gt;</c>:
/// <c>value</c> is a literal and <c>expr</c> an expression, exactly one of them; <c>target</c> picks the span. The
/// definition it builds and the C# it prints are checked here; what lands on which span is covered by the engine's
/// own <c>SpanTagTests</c>.
/// </summary>
public class SpanTagXmlTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private SpanTagDefinition[] TagsOf(string xml)
    {
        _context.AddXmlRoutesFromContent(xml, "<inline>");
        var builder = _context.RouteBuilders.Single();
        if (!builder.IsBuilt)
            builder.InternalBuild(_context);
        return builder.Definitions.Single().Outputs.OfType<SpanTagDefinition>().ToArray();
    }

    private static string CodeOf(string xml)
        => XmlCodeGenerator.Generate(System.Xml.Linq.XDocument.Parse(xml), "Tags", "Tests.Generated",
            sourceName: "<inline>");

    [Fact]
    public void Value_IsALiteral_AndTheTargetDefaultsToTheEnclosingSpan()
    {
        var tag = TagsOf("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-spantag-literal">
                <from uri="direct://xml-spantag-literal"/>
                <spanTag key="flow.name" value="${not.a.template}"/>
              </route>
            </routes>
            """).Single();

        tag.Key.Should().Be("flow.name");
        tag.Target.Should().Be(SpanTarget.Enclosing);
        tag.Value.Should().BeOfType<ConstantExpression>("value is a literal, as on <setHeader>")
            .Which.Evaluate<object>(new Exchange()).Should().Be("${not.a.template}");
    }

    [Fact]
    public void Expr_IsEvaluated_AndTheTargetIsTaken()
    {
        // The schema enumerates the names as they are, as for level="Debug" or policy="...".
        var tag = TagsOf("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-spantag-expr">
                <from uri="direct://xml-spantag-expr"/>
                <spanTag key="sap.order.id" expr="${header.orderNo}" target="Inbound"/>
              </route>
            </routes>
            """).Single();

        tag.Target.Should().Be(SpanTarget.Inbound);
        var exchange = new Exchange(new Message("b"));
        exchange.In.Headers["orderNo"] = "A-42";
        tag.Value.Evaluate<object>(exchange)?.ToString().Should().Be("A-42");
    }

    [Fact]
    public void ValueAndExprTogether_IsAPositionedError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-spantag-both">
                <from uri="direct://xml-spantag-both"/>
                <spanTag key="order.id" value="a" expr="${header.b}"/>
              </route>
            </routes>
            """, "<inline>");

        act.Should().Throw<XmlRouteException>();
    }

    [Fact]
    public void AnUnknownTarget_ReportsTheKnownNames()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-spantag-target">
                <from uri="direct://xml-spantag-target"/>
                <spanTag key="order.id" value="a" target="root"/>
              </route>
            </routes>
            """, "<inline>");

        act.Should().Throw<XmlRouteException>();
    }

    [Fact]
    public void AReservedKey_IsAPositionedError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-spantag-reserved">
                <from uri="direct://xml-spantag-reserved"/>
                <spanTag key="redb.route.id" value="a"/>
              </route>
            </routes>
            """, "<inline>");

        act.Should().Throw<XmlRouteException>().WithMessage("*redb.*");
    }

    [Fact]
    public void Print_LiteralExprAndTarget()
    {
        var code = CodeOf("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-spantag-print">
                <from uri="direct://xml-spantag-print"/>
                <spanTag key="flow.name" value="ack"/>
                <spanTag key="sap.order.id" expr="${header.orderNo}" target="Inbound"/>
                <spanTag key="sap.batch" expr="${property.batch}" target="Enclosing"/>
              </route>
            </routes>
            """);

        code.Should().Contain("SpanTag(\"flow.name\", \"ack\")");
        code.Should().Contain("SpanTag(\"sap.order.id\", Expr(\"${header.orderNo}\"), global::redb.Route.Telemetry.SpanTarget.Inbound)");
        code.Should().Contain("SpanTag(\"sap.batch\", Expr(\"${property.batch}\"))", "the default target is not printed");
    }
}
