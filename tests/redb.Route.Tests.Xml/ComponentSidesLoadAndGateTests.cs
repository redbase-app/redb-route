using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// A scheme on the side its component lacks (<c>[EndpointRole]</c> on the class) is refused at load and by
/// <c>check</c>, at its position, in the words the runtime uses.
/// </summary>
public sealed class ComponentSidesLoadAndGateTests : IAsyncLifetime, IDisposable
{
    private readonly RouteContext _context = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-sides-" + Guid.NewGuid().ToString("N"));

    public ComponentSidesLoadAndGateTests() => Directory.CreateDirectory(_root);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Action Load(string from, string to) => () => _context.AddXmlRoutesFromContent($"""
        <routes xmlns="urn:redb:route:1.0">
          <route id="r">
            <from{from}
            <to{to}
          </route>
        </routes>
        """, "sides.xml");

    [Fact]
    public void Loader_refuses_a_route_from_a_producer_only_scheme_in_the_uri_form()
        => Load(""" uri="log://x"/>""", """ uri="direct://out"/>""").Should().Throw<XmlRouteException>()
            .WithMessage("*sides.xml(3,*<from>: log has no consumer side: a route cannot start from it (log is producer-only).*");

    [Fact]
    public void Loader_refuses_a_route_from_a_producer_only_scheme_in_the_structured_form()
        => Load("""><log path="x"/></from>""", """ uri="direct://out"/>""").Should().Throw<XmlRouteException>()
            .WithMessage("*sides.xml(3,*<from>: log has no consumer side*");

    [Fact]
    public void Loader_refuses_a_target_that_is_consumer_only()
        => Load(""" uri="direct://in"/>""", """><timer path="t"/></to>""").Should().Throw<XmlRouteException>()
            .WithMessage("*sides.xml(4,*<to>: timer has no producer side: a route cannot send to it (timer is consumer-only). Use it as a From() source.*");

    [Fact]
    public void Loader_takes_each_scheme_on_its_own_side()
        => Load(""" uri="timer://t?period=3600000&amp;delay=3600000"/>""", """ uri="log://x"/>""").Should().NotThrow();

    [Fact]
    public void Gate_reports_a_scheme_on_its_missing_side_in_both_forms_without_bin()
    {
        var dir = RouteProjectScaffold.Create(_root, "Sides");
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="uri-form">
                <from uri="log://x"/>
                <to uri="timer://t"/>
              </route>
              <route id="structured-form">
                <from><log path="y"/></from>
                <to><timer path="u"/></to>
              </route>
              <route id="right">
                <from uri="timer://v"/>
                <to uri="log://z"/>
              </route>
            </routes>
            """);

        var errors = RoutePackage.Check(dir, "p", "1.0.0").Errors.ToList();

        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(3,")
            && e.Message.Contains("<from>: log has no consumer side: a route cannot start from it"));
        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(4,")
            && e.Message.Contains("<to>: timer has no producer side: a route cannot send to it"));
        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(7,") && e.Message.Contains("<from>: log has no consumer side"));
        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(8,") && e.Message.Contains("<to>: timer has no producer side"));
        errors.Should().NotContain(e => e.File.StartsWith("routes/main.route.xml(11,") || e.File.StartsWith("routes/main.route.xml(12,"));
    }
}
