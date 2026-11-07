using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// An unclosed <c>${</c> is an error (owner decision 2026-11-05): a route file fails to load at the element, and the
/// package gate reports it at the attribute, in every place that holds an expression or an address.
/// </summary>
public sealed class UnclosedPlaceholderTests : IAsyncLifetime, IDisposable
{
    private readonly RouteContext _context = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-unclosed-" + Guid.NewGuid().ToString("N"));

    public UnclosedPlaceholderTests() => Directory.CreateDirectory(_root);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Type? Resolve(string typeName) => Type.GetType(typeName);

    private string Project(string steps)
    {
        var dir = RouteProjectScaffold.Create(_root, "Unclosed" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="unclosed">
                <from uri="direct://in"/>
            {steps}
              </route>
            </routes>
            """);
        return dir;
    }

    [Fact]
    public void Load_FailsAtTheElement_WithThePosition()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="unclosed-load">
                <from uri="direct://unclosed-load"/>
                <setBody expr="total ${header.amount"/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*(4,*unclosed '${' at position 7 of \"total ${header.amount\": the placeholder has no closing '}'.*");
    }

    [Theory]
    [InlineData("""<setHeader name="h" expr="${header.a"/>""", "expr=")]
    [InlineData("""<filter expr="${header.go == 'yes'"><to uri="log:x"/></filter>""", "expr=")]
    [InlineData("""<toD uri="log:${header.name"/>""", "uri=")]
    [InlineData("""<to><log path="x" message="${body"/></to>""", "message=")]
    public void Gate_ReportsAnError_AtTheAttribute(string step, string attribute)
    {
        var errors = RoutePackage.Check(Project(step), "p", "1.0.0", Resolve).Errors.ToList();

        errors.Should().Contain(e => e.Message.StartsWith(attribute + " unclosed '${'", StringComparison.Ordinal)
                                     && e.File.Contains("main.route.xml("));
    }

    [Fact]
    public void Gate_ReportsTheFromAddress()
    {
        var dir = RouteProjectScaffold.Create(_root, "UnclosedFrom");
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="unclosed-from">
                <from uri="timer://t?period=${header.p"/>
                <to uri="log:x"/>
              </route>
            </routes>
            """);

        RoutePackage.Check(dir, "p", "1.0.0", Resolve).Errors
            .Should().Contain(e => e.Message.StartsWith("uri= unclosed '${'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""<setHeader name="h" value="literal ${ text"/>""")]
    [InlineData("""<setHeader name="h" expr="${header.a} and ${header.b}"/>""")]
    [InlineData("""<setHeader name="h" expr="${header.a == '}'}"/>""")]
    [InlineData("""<setBody expr="echo \${HOME} for ${header.user}"/>""")]
    public void Gate_LeavesLiteralsAndClosedPlaceholdersAlone(string step)
    {
        RoutePackage.Check(Project(step), "p", "1.0.0", Resolve).Errors
            .Should().NotContain(e => e.Message.Contains("unclosed '${'", StringComparison.Ordinal));
    }
}
