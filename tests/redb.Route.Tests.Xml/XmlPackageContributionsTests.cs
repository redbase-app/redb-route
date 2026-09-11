using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.JsonTransform;
using redb.Route.Templates;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The first REAL package contributions through the Р21 channel — `redb.Route.Cache`,
/// `redb.Route.JsonTransform`, `redb.Route.Templates` each ship their element (parse + schema
/// shape + C# printing in one class) and a host registers them via
/// <see cref="XmlRouteLoaderOptions.Extensions"/>. This closes the last "package elements" rows
/// of the Ф0 §4 table reachable before Tsak (`&lt;rest&gt;` waits for the Http package to be
/// free, `redb.Route.Core` waits for the owner).
/// </summary>
public class XmlPackageContributionsTests : IAsyncDisposable
{
    private static readonly XmlRouteLoaderOptions Options = new()
    {
        Extensions = [new CacheXmlContribution(), new JsonTransformXmlContribution(), new PayloadXmlContribution()],
    };

    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<IProducer> StartAndProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task Cache_Element_CachesTheScopeResult()
    {
        _context.UseCache();
        var inner = 0;
        _context.AddRoutes(r => r.From("direct://cache-inner").Process(e =>
        {
            inner++;
            e.In.Body = $"computed-{e.In.Headers["k"]}";
        }));
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="pkg-cache">
                <from uri="direct://pkg-cache-in"/>
                <cache key="${header.k}" ttl="00:01:00">
                  <to uri="direct://cache-inner"/>
                </cache>
              </route>
            </routes>
            """, options: Options);
        var producer = await StartAndProducer("direct://pkg-cache-in");

        var first = Msg("a");
        await producer.Process(first);
        var second = Msg("a");
        await producer.Process(second);
        var other = Msg("b");
        await producer.Process(other);

        inner.Should().Be(2, "the second send with the same key is a cache hit");
        second.In.Body.Should().Be("computed-a");
        other.In.Body.Should().Be("computed-b");
    }

    [Fact]
    public async Task TransformJson_Element_RunsInlineJsonata()
    {
        _context.UseJsonTransform();
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="pkg-jsonata">
                <from uri="direct://pkg-json-in"/>
                <transformJson><![CDATA[ { "who": $uppercase(name) } ]]></transformJson>
              </route>
            </routes>
            """, options: Options);
        var producer = await StartAndProducer("direct://pkg-json-in");

        var exchange = new Exchange(new Message("""{"name":"bob"}"""));
        await producer.Process(exchange);

        exchange.In.Body.Should().BeOfType<string>().Which.Should().Contain("\"who\"").And.Contain("BOB");
    }

    [Fact]
    public async Task Payload_Element_RendersTheTemplate_WithArgs()
    {
        _context.UseTemplates();
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="pkg-payload">
                <from uri="direct://pkg-payload-in"/>
                <payload mediaType="text" target="header:greeting">
                  <arg name="who" expr="${header.user}"/>
                  <![CDATA[hello {{ args.who }}]]>
                </payload>
              </route>
            </routes>
            """, options: Options);
        var producer = await StartAndProducer("direct://pkg-payload-in");

        var exchange = Msg("x", ("user", "rinat"));
        await producer.Process(exchange);

        exchange.In.Headers["greeting"].Should().Be("hello rinat");
    }

    [Fact]
    public void PackageElements_ValidateAgainstTheSchema_ThroughTheirSpecs()
    {
        // The schema pass is ON: these load only because each contribution declares its shape.
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="pkg-bad">
                <from uri="direct://pkg-bad-in"/>
                <cache key="${header.k}" provder="memory"><removeBody/></cache>
              </route>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>().WithMessage("*[schema]*provder*",
            "a typo in a package element's attribute is caught by the generated schema");
    }

    [Fact]
    public void PackageElements_PrintThroughTheGenerator()
    {
        var code = XmlCodeGenerator.Generate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="pkg-gen">
                <from uri="direct://pkg-gen-in"/>
                <cache key="${header.k}" ttl="00:01:00" provider="distributed">
                  <transformJson spec="specs/normalize.jsonata"/>
                </cache>
                <payload mediaType="json"><![CDATA[{ "ok": true }]]></payload>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), "PkgRoutes", "Demo", options: Options);

        code.Should().Contain(".Cache(\"${header.k}\", TimeSpan.FromSeconds(60));")
            .And.Contain("Distributed()")
            .And.Contain("TransformJson(\"specs/normalize.jsonata\")")
            .And.Contain("SetBodyTemplate(TextSource.Inline(");
    }

    // ── <rest>: the container-level contribution of the Http package ─────────

    private static readonly XmlRouteLoaderOptions RestOptions = new()
    {
        Extensions = [new Route.Http.Rest.RestXmlContribution()],
    };

    [Fact]
    public void Rest_SpawnsTheHttpRoutes_ForToAndInlineVerbs()
    {
        _context.AddComponent(new KafkaStubComponent()); // registers "kafka" for the to= check
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="orders-handler">
                <from uri="direct://orders-handler"/>
                <setBody value="handled"/>
              </route>
              <rest path="/api/orders" port="5099" bindingMode="json" openApi="false">
                <get path="/{id}" id="orders-get" to="direct://orders-handler"/>
                <post produces="application/json">
                  <setHeader name="accepted" value="true"/>
                  <to uri="kafka://orders"/>
                </post>
              </rest>
            </routes>
            """, options: RestOptions);

        var ids = Examples.ExampleHarness.Definitions(_context)
            .OfType<IRouteDefinition>().Select(r => r.GetRouteId()).ToList();
        ids.Should().Contain("orders-get", "id= names the verb's route");
        ids.Should().Contain(id => id != null && id.StartsWith("rest:POST /api/orders"),
            "the inline verb spawns its consumer route");
        ids.Should().Contain(id => id != null && id.Contains("(handler)"),
            "inline steps live in their own handler route");
    }

    [Fact]
    public void Rest_VerbWithBothToAndSteps_IsASchemaError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <rest path="/api" openApi="false">
                <get to="direct://x"><removeBody/></get>
              </rest>
            </routes>
            """, options: RestOptions);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*to= or with inline steps*not both*");
    }

    [Fact]
    public void Rest_InsideARoute_IsRefusedWithAClearMessage()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad">
                <from uri="direct://bad-in"/>
                <rest path="/api"/>
              </route>
            </routes>
            """, options: RestOptions);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*container-level element*beside <route>*");
    }

    [Fact]
    public void Rest_PrintsThroughTheGenerator()
    {
        var code = XmlCodeGenerator.Generate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <rest path="/api" port="5099">
                <get path="/{id}" to="direct://x"/>
                <post><removeBody/></post>
              </rest>
            </routes>
            """, LoadOptions.SetLineInfo), "RestRoutes", "Demo", options: RestOptions);

        code.Should().Contain("Rest(\"/api\", options => { options.Port = 5099; })")
            .And.Contain(".Get(\"/{id}\").To(\"direct://x\");")
            .And.Contain(".Post(\"\").Route()");
    }

    private static IExchange Msg(object? body, params (string Name, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message(body));
        foreach (var (name, value) in headers) exchange.In.Headers[name] = value;
        return exchange;
    }

    private static IExchange Msg(string key)
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["k"] = key;
        return exchange;
    }
}
