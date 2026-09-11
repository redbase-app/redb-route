using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Route-XML Ф2, container batch: handlers on &lt;routes&gt; for every route of the file, the
/// Р21 extension channel exercised from outside the core, expression-escaping equivalence and
/// the glob loading overload.
/// </summary>
public class XmlContainerAndLoadingTests : IAsyncDisposable
{
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

    // ── container-level handlers ─────────────────────────────────────────────

    [Fact]
    public async Task ContainerOnException_AppliesToEveryRouteOfTheFile()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <onException exceptions="System.InvalidOperationException" handled="true">
                <setHeader name="rescued" value="yes"/>
              </onException>
              <route id="cont-a">
                <from uri="direct://cont-a-in"/>
                <throwException type="System.InvalidOperationException" message="a"/>
              </route>
              <route id="cont-b">
                <from uri="direct://cont-b-in"/>
                <throwException type="System.InvalidOperationException" message="b"/>
              </route>
            </routes>
            """);
        await _context.Start();
        var producerA = _context.GetEndpoint("direct://cont-a-in").CreateProducer();
        var producerB = _context.GetEndpoint("direct://cont-b-in").CreateProducer();
        await producerA.Start();
        await producerB.Start();

        var a = new Exchange(new Message("x"));
        await producerA.Process(a);
        var b = new Exchange(new Message("x"));
        await producerB.Process(b);

        a.In.Headers["rescued"].Should().Be("yes");
        b.In.Headers["rescued"].Should().Be("yes");
    }

    [Fact]
    public async Task ContainerIntercept_RunsForEveryRouteOfTheFile()
    {
        var seen = new List<object?>();
        _context.AddRoutes(r => r.From("direct://cont-i-sink").Process(e => seen.Add(e.In.Headers["tapped"])));
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <interceptFrom>
                <setHeader name="tapped" value="yes"/>
              </interceptFrom>
              <route id="cont-i-1">
                <from uri="direct://cont-i-1-in"/>
                <to uri="direct://cont-i-sink"/>
              </route>
              <route id="cont-i-2">
                <from uri="direct://cont-i-2-in"/>
                <to uri="direct://cont-i-sink"/>
              </route>
            </routes>
            """);
        await _context.Start();
        foreach (var uri in new[] { "direct://cont-i-1-in", "direct://cont-i-2-in" })
        {
            var producer = _context.GetEndpoint(uri).CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("x")));
        }

        seen.Should().Equal("yes", "yes");
    }

    [Fact]
    public void SkipSendToOriginalEndpoint_OnAPlainIntercept_IsASchemaError()
    {
        // The attribute only means something on <interceptSendToEndpoint>; on the other
        // intercepts it is a misplaced intention, not something to apply silently.
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cont-skip">
                <from uri="direct://cont-skip-in"/>
                <intercept skipSendToOriginalEndpoint="true"><removeBody/></intercept>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*skipSendToOriginalEndpoint*only*interceptSendToEndpoint*");
    }

    // ── the Р21 extension channel from outside the core ──────────────────────

    private sealed class StampContribution : IXmlElementContribution
    {
        public string Name => "stamp";
        public XmlElementKind Kind => XmlElementKind.Step;
        // The spec is what admits the attribute past the generated schema (Ф4 §3.4):
        // an opaque contribution parses, but its attributes fail validation — by design.
        public ElementSpec Spec => ElementSpec.Leaf("stamp", new AttributeSpec("value", AttributeType.String));
        public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
            => current.SetHeader("stamped", context.Attr(element, "value") ?? "yes");
    }

    private sealed class DuplicateToContribution : IXmlElementContribution
    {
        public string Name => "to";
        public XmlElementKind Kind => XmlElementKind.Step;
        public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
            => current;
    }

    [Fact]
    public async Task ExternalContribution_Registered_ParsesLikeACoreElement()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ext-ok">
                <from uri="direct://ext-in"/>
                <stamp value="external"/>
              </route>
            </routes>
            """, options: new XmlRouteLoaderOptions { Extensions = [new StampContribution()] });
        var producer = await StartAndProducer("direct://ext-in");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.In.Headers["stamped"].Should().Be("external");
    }

    [Fact]
    public void ExternalContribution_NotRegistered_ItsElementIsRejected()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ext-missing">
                <from uri="direct://ext-missing-in"/>
                <stamp/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*unknown element <stamp>*");
    }

    [Fact]
    public void DuplicateElementName_IsAHardRegistrationError()
    {
        var act = () => _context.AddXmlRoutesFromContent(
            """<routes xmlns="urn:redb:route:1.0"/>""",
            options: new XmlRouteLoaderOptions { Extensions = [new DuplicateToContribution()] });

        act.Should().Throw<InvalidOperationException>().WithMessage("*'to' is already registered*");
    }

    // ── expression escaping equivalence (Ф0 §6.2) ────────────────────────────

    [Fact]
    public async Task EscapedAndBareGreaterThan_AreTheSameExpression()
    {
        var passed = new List<string>();
        _context.AddRoutes(r => r.From("direct://esc-sink").Process(e => passed.Add((string)e.In.Body!)));
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="esc-bare">
                <from uri="direct://esc-bare-in"/>
                <filter expr="header.a>10"><to uri="direct://esc-sink"/></filter>
              </route>
              <route id="esc-escaped">
                <from uri="direct://esc-escaped-in"/>
                <filter expr="header.a &gt; 10"><to uri="direct://esc-sink"/></filter>
              </route>
            </routes>
            """);
        await _context.Start();
        foreach (var (uri, tag) in new[] { ("direct://esc-bare-in", "bare"), ("direct://esc-escaped-in", "escaped") })
        {
            var producer = _context.GetEndpoint(uri).CreateProducer();
            await producer.Start();
            var over = new Exchange(new Message(tag));
            over.In.Headers["a"] = 20;
            await producer.Process(over);
            var under = new Exchange(new Message(tag + "-under"));
            under.In.Headers["a"] = 5;
            await producer.Process(under);
        }

        passed.Should().Equal("bare", "escaped");
    }

    // ── glob loading ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GlobPattern_LoadsEveryMatchingFile_Deterministically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "redb-xml-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.route.xml"), """
                <routes xmlns="urn:redb:route:1.0">
                  <route id="glob-a"><from uri="direct://glob-a-in"/><removeBody/></route>
                </routes>
                """);
            File.WriteAllText(Path.Combine(dir, "b.route.xml"), """
                <routes xmlns="urn:redb:route:1.0">
                  <route id="glob-b"><from uri="direct://glob-b-in"/><removeBody/></route>
                </routes>
                """);
            File.WriteAllText(Path.Combine(dir, "ignored.txt"), "not a route file");

            _context.AddXmlRoutes(Path.Combine(dir, "*.route.xml"));
            await _context.Start();

            _context.Routes.Select(r => r.RouteId).Should().Contain(["glob-a", "glob-b"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GlobPattern_MatchingNothing_IsAnError_NamingTheSearchedPlaces()
    {
        var dir = Path.Combine(Path.GetTempPath(), "redb-xml-glob-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var act = () => _context.AddXmlRoutes(Path.Combine(dir, "*.route.xml"));

            act.Should().Throw<XmlRouteException>()
                .WithMessage("*no route files match*Searched:*");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
