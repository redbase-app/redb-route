using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Route-XML Ф2, §7.2: the structured endpoint form — one generic normalization into the same
/// URI string a hand-written address would be, for every scheme, with zero per-connector code.
/// Exact-string assertions on the normalizer plus behavioral proof that the canon is identical
/// (a structured address and a hand-written URI meet at the same endpoint).
/// </summary>
public class StructuredEndpointTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ── the normalizer, exact strings ────────────────────────────────────────

    private string? Resolve(string stepXml, out List<string> errors)
    {
        var ctx = new XmlParseContext(_context, ElementRegistry.CreateDefault(), "probe.xml");
        var step = XElement.Parse(stepXml, LoadOptions.SetLineInfo);
        var uri = StructuredEndpoint.Resolve(step, ctx, out _);
        errors = [.. ctx.Errors];
        return uri;
    }

    [Fact]
    public void Attributes_BecomeQueryOptions_PathAttributeIsThePath()
    {
        var uri = Resolve("""
            <to><kafka path="orders" key="${header.tripId}" groupId="orders-svc"/></to>
            """, out var errors);

        errors.Should().BeEmpty();
        uri.Should().Be("kafka://orders?key=${header.tripId}&groupId=orders-svc");
    }

    [Fact]
    public void CdataPath_ParamFamily_AndTextOption_AllNormalizeIntoOptions()
    {
        var uri = Resolve("""
            <to>
              <sql dataSource="#main-db">
                <![CDATA[SELECT * FROM outbox WHERE sent = 0]]>
                <param name="login" value="${header.login}"/>
                <onSuccess><![CDATA[UPDATE outbox SET sent = 1 AND flags = 2]]></onSuccess>
              </sql>
            </to>
            """, out var errors);

        errors.Should().BeEmpty();
        uri.Should().Be("sql://SELECT * FROM outbox WHERE sent = 0" +
                        "?dataSource=#main-db" +
                        "&param.login=${header.login}" +
                        "&onSuccess=UPDATE outbox SET sent = 1 AND flags = 2");
    }

    [Fact]
    public void AmpersandAndPercent_InOptionValues_SurviveTheEngineRoundTrip()
    {
        var uri = Resolve("""
            <to>
              <sql path="q">
                <onSuccess><![CDATA[UPDATE t SET n = a & b WHERE p LIKE '%x%']]></onSuccess>
              </sql>
            </to>
            """, out var errors);

        errors.Should().BeEmpty();
        // Only the two characters the query parse cannot survive are encoded…
        uri.Should().Be("sql://q?onSuccess=UPDATE t SET n = a %26 b WHERE p LIKE '%25x%25'");
        // …and the engine's own parser hands the original text back.
        EndpointUriParser.Parse(uri!).RawParameters["onSuccess"]
            .Should().Be("UPDATE t SET n = a & b WHERE p LIKE '%x%'");
    }

    [Fact]
    public void UriAndChild_Together_IsASchemaError()
    {
        Resolve("""<to uri="direct://x"><kafka path="orders"/></to>""", out var errors);
        errors.Should().ContainSingle().Which.Should().Contain("either uri= or a structured endpoint child");
    }

    [Fact]
    public void TwoEndpointChildren_IsASchemaError()
    {
        Resolve("""<to><kafka path="a"/><kafka path="b"/></to>""", out var errors);
        errors.Should().ContainSingle().Which.Should().Contain("exactly one endpoint child");
    }

    [Fact]
    public void NeitherUriNorChild_IsASchemaError()
    {
        Resolve("""<to/>""", out var errors);
        errors.Should().ContainSingle().Which.Should().Contain("requires the uri attribute or one endpoint child");
    }

    [Fact]
    public void PathAttributeAndContent_Together_IsASchemaError()
    {
        Resolve("""<to><sql path="a">SELECT 1</sql></to>""", out var errors);
        errors.Should().ContainSingle().Which.Should().Contain("carries its path more than once");
    }

    [Fact]
    public void QuestionMarkInPath_IsASchemaError()
    {
        Resolve("""<to><sql><![CDATA[SELECT * FROM t WHERE a = ?]]></sql></to>""", out var errors);
        errors.Should().ContainSingle().Which.Should().Contain("contains '?'");
    }

    [Fact]
    public void MalformedOptionChild_NamesBothValidShapes()
    {
        Resolve("""<to><sql path="q"><param name="x"/></sql></to>""", out var errors);
        errors.Should().ContainSingle().Which
            .Should().Contain("family entry").And.Contain("text option");
    }

    // ── the canon is one: structured and hand-written meet at the same endpoint ──

    [Fact]
    public async Task StructuredTo_DeliversToTheHandWrittenUriConsumer()
    {
        object? delivered = null;
        _context.AddRoutes(r => r.From("direct://s72-sink").Process(e => delivered = e.In.Body));
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="s72-to">
                <from uri="direct://s72-in"/>
                <to><direct path="s72-sink"/></to>
              </route>
            </routes>
            """);
        await _context.Start();
        var producer = _context.GetEndpoint("direct://s72-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("canon")));

        delivered.Should().Be("canon");
    }

    [Fact]
    public async Task StructuredFrom_IsAConsumerLikeAnyOther()
    {
        var seen = new List<object?>();
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="s72-from">
                <from><direct path="s72-from-in"/></from>
                <setHeader name="via" value="structured"/>
              </route>
            </routes>
            """);
        await _context.Start();
        var producer = _context.GetEndpoint("direct://s72-from-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);
        seen.Add(exchange.In.Headers["via"]);

        seen.Should().Equal("structured");
    }

    [Fact]
    public void StructuredFrom_WithPlaceholderInAnOption_IsRefusedLikeTheUriForm()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="s72-badfrom">
                <from><seda path="q" concurrentConsumers="${header.n}"/></from>
                <removeBody/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*consumer URI cannot carry ${*");
    }

    [Fact]
    public void UnregisteredStructuredScheme_IsCaughtAtLoad_AtTheChildPosition()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="s72-badscheme">
                <from uri="direct://s72-bs-in"/>
                <to>
                  <sql dataSource="#db"><![CDATA[SELECT 1]]></sql>
                </to>
              </route>
            </routes>
            """, "structured.xml");

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*structured.xml(5,*scheme 'sql' is not registered*");
    }
}
