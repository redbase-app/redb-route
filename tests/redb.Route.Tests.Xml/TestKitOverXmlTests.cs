using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.TestKit;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>A registration-only stand-in for an external broker scheme.</summary>
public sealed class KafkaStubComponent : ComponentBase
{
    public override string Scheme => "kafka";
    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException("the test mocks this endpoint before anything creates it");
}

/// <summary>
/// Route-XML Ф3.1: an XML route is tested the same way a C# route is — the existing TestKit
/// (advice, endpoint mocking, weaving, one-line send) over the Ф2 loader, with no test host of
/// our own (Ф3 §1–2). Includes the two guarantees the phase names explicitly: WeaveById finds
/// steps by the XML id attribute, and a URI password never surfaces in mock names or reports.
/// </summary>
public class TestKitOverXmlTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public TestKitOverXmlTests() => _context.AddComponent(new KafkaStubComponent());

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheCanonicalTestKitFlow_WorksOverAnXmlRoute()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="under-test">
                <from uri="direct://tk-in"/>
                <setHeader name="seen" value="true"/>
                <to uri="kafka://orders"/>
              </route>
            </routes>
            """);
        _context.AdviceAllRoutes(a => a.MockEndpoints("kafka://*"));
        await _context.Start();

        var mock = _context.Mock("kafka://orders").ExpectMessageCount(1).ExpectHeader("seen", "true");
        await _context.SendBody("direct://tk-in", "payload");

        await mock.AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WeaveById_FindsTheXmlStep_ByItsIdAttribute()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="weave-me">
                <from uri="direct://tk-weave-in"/>
                <setHeader id="mark-step" name="mark" value="original"/>
                <to uri="kafka://weave"/>
              </route>
            </routes>
            """);
        _context.AdviceRoute("weave-me", a =>
        {
            a.WeaveById("mark-step").Replace(d => d.SetHeader("mark", "woven"));
            a.MockEndpoints("kafka://*");
        });
        await _context.Start();

        var mock = _context.Mock("kafka://weave").ExpectMessageCount(1).ExpectHeader("mark", "woven");
        await _context.SendBody("direct://tk-weave-in", "x");

        await mock.AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PasswordFromTheUri_NeverSurfaces_InMockNameOrAssertReport()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="secret-route">
                <from uri="direct://tk-secret-in"/>
                <to uri="kafka://orders?password=hush-hush"/>
              </route>
            </routes>
            """);
        _context.AdviceAllRoutes(a => a.MockEndpoints("kafka://*"));
        await _context.Start();

        MockUri.For("kafka://orders?password=hush-hush").Should().NotContain("hush-hush",
            "the query string, where passwords live, must not become part of the mock name");

        // A failing assertion writes its report into the exception message — the place it
        // would leak from into CI logs.
        var mock = _context.Mock("kafka://orders?password=hush-hush").ExpectMessageCount(2);
        await _context.SendBody("direct://tk-secret-in", "only-one");

        var act = () => mock.AssertIsSatisfiedAsync(TimeSpan.FromMilliseconds(300));
        var report = (await act.Should().ThrowAsync<Exception>()).Which.ToString();
        report.Should().NotContain("hush-hush");
    }

    [Fact]
    public async Task TwoContexts_DoNotSeeEachOthersXmlRoutes()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="iso-a"><from uri="direct://iso-a-in"/><removeBody/></route>
            </routes>
            """);
        await using var other = new RouteContext();
        other.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="iso-b"><from uri="direct://iso-b-in"/><removeBody/></route>
            </routes>
            """);
        await _context.Start();
        await other.Start();

        _context.Routes.Select(r => r.RouteId).Should().Contain("iso-a").And.NotContain("iso-b");
        other.Routes.Select(r => r.RouteId).Should().Contain("iso-b").And.NotContain("iso-a");
    }
}
