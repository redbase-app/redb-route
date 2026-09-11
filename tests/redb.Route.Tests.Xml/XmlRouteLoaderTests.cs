using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>A public bean target for the &lt;bean&gt; + bean: tests.</summary>
public class XmlGreeter
{
    public string Prefix { get; set; } = "hi";
    public string Greet(IExchange exchange) => $"{Prefix}:{exchange.In.Body}";
}

/// <summary>
/// Route-XML Ф2, first vertical slice: an XML document becomes a working route through the
/// existing DSL — with every error of a document collected in one pass, positions included,
/// and the load-time checks the format promises (consumer ${...}, unregistered schemes).
/// </summary>
public class XmlRouteLoaderTests : IAsyncDisposable
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

    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task SimpleRoute_LoadsAndRuns_ThroughTheExistingDsl()
    {
        object? delivered = null;
        _context.AddRoutes(r => r.From("direct://xml-out").Process(e => delivered = e.In.Body));
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-simple">
                <from uri="direct://xml-in"/>
                <setHeader name="tag" value="loaded"/>
                <setBody expr="${header.tag}:${body}"/>
                <to uri="direct://xml-out"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://xml-in");

        await producer.Process(new Exchange(new Message("payload")));

        delivered.Should().Be("loaded:payload");
    }

    [Fact]
    public async Task ChoiceAndFilter_BranchAndGuard()
    {
        var seen = new List<string>();
        _context.AddRoutes(r =>
        {
            r.From("direct://xml-vip").Process(_ => seen.Add("vip"));
            r.From("direct://xml-std").Process(_ => seen.Add("std"));
        });
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-choice">
                <from uri="direct://xml-router"/>
                <filter expr="header.amount &gt; 0">
                  <choice>
                    <when expr="header.amount &gt; 1000">
                      <to uri="direct://xml-vip"/>
                    </when>
                    <otherwise>
                      <to uri="direct://xml-std"/>
                    </otherwise>
                  </choice>
                </filter>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://xml-router");

        await producer.Process(Msg(("amount", 5000)));
        await producer.Process(Msg(("amount", 10)));
        await producer.Process(Msg(("amount", -1))); // filtered out

        seen.Should().Equal("vip", "std");
    }

    [Fact]
    public async Task BeanSection_DeclaresTheObject_AndBeanUriCallsIt()
    {
        _context.AddXmlRoutesFromContent($$"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="greeter" type="{{typeof(XmlGreeter).FullName}}, {{typeof(XmlGreeter).Assembly.GetName().Name}}">
                <property key="Prefix" value="xml"/>
              </bean>
              <route id="xml-bean">
                <from uri="direct://xml-bean-in"/>
                <to uri="bean:#greeter?method=Greet"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://xml-bean-in");

        var exchange = new Exchange(new Message("bob"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("xml:bob");
    }

    [Fact]
    public async Task RouteAttributes_IdAndMessageHistoryAndStepIdentity_FlowThrough()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-history" messageHistory="true">
                <from uri="direct://xml-hist-in"/>
                <setHeader id="mark-step" description="Mark the exchange" name="mark" value="1"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://xml-hist-in");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        var entry = MessageHistory.GetEntries(exchange).Should().ContainSingle().Subject;
        entry.RouteId.Should().Be("xml-history");
        entry.NodeId.Should().Be("mark-step");
        entry.Label.Should().Be("Mark the exchange");
    }

    [Fact]
    public async Task EnabledFalse_DoesNotRegisterTheRoute()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-disabled" enabled="false">
                <from uri="direct://xml-disabled-in"/>
                <to uri="direct://nowhere"/>
              </route>
              <route id="xml-enabled">
                <from uri="direct://xml-enabled-in"/>
                <removeBody/>
              </route>
            </routes>
            """);
        await _context.Start();

        _context.Routes.Select(r => r.RouteId).Should().Contain("xml-enabled").And.NotContain("xml-disabled");
    }

    // ── Errors: one pass, positions, refusal ─────────────────────────────────

    [Fact]
    public void UnknownElement_ReportsPositionAndDidYouMean()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad">
                <from uri="direct://bad-in"/>
                <setHeaderr name="x" value="1"/>
              </route>
            </routes>
            """, "routes/bad.route.xml");

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*routes/bad.route.xml(4,*setHeaderr*Did you mean <setHeader>?*");
    }

    [Fact]
    public void AllErrorsOfTheDocument_AreCollectedInOnePass()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="multi">
                <from uri="direct://multi-in"/>
                <setHeader name="a" value="1" expr="header.b"/>
                <nosuch/>
                <to uri="unregistered-scheme://queue"/>
              </route>
            </routes>
            """);

        var errors = act.Should().Throw<XmlRouteException>().Which.Errors;
        errors.Should().HaveCount(3);
        errors.Should().Contain(e => e.Contains("'value' or 'expr', not both"));
        errors.Should().Contain(e => e.Contains("unknown element <nosuch>"));
        errors.Should().Contain(e => e.Contains("scheme 'unregistered-scheme' is not registered"));
    }

    [Fact]
    public void ConsumerUriWithPlaceholder_IsRefusedAtLoad()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="badfrom">
                <from uri="direct://in-${header.region}"/>
                <removeBody/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*consumer URI cannot carry ${*only constants and {{key}}*");
    }

    [Fact]
    public void MalformedExpression_FailsAtLoad_WithThePosition()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="badexpr">
                <from uri="direct://badexpr-in"/>
                <filter expr="header.amount &gt;">
                  <removeBody/>
                </filter>
              </route>
            </routes>
            """, "bad-expr.xml");

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*bad-expr.xml(4,*");
    }

    [Fact]
    public void ForeignNamespaceElement_IsRejected_NotSilentlyParsedByLocalName()
    {
        // The XSD/namespace discipline: an element from another namespace must never pass as a
        // core element just because the local names match.
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0" xmlns:alien="urn:someone:else">
              <route id="ns-alien">
                <from uri="direct://ns-alien-in"/>
                <alien:setHeader name="a" value="1"/>
              </route>
            </routes>
            """, "alien.xml");

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*alien.xml(4,*<setHeader>*namespace 'urn:someone:else'*urn:redb:route:1.0*");
    }

    [Fact]
    public void EmptyNamespaceSubtree_IsRejectedToo()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ns-empty" xmlns="">
                <from uri="direct://ns-empty-in"/>
                <removeBody/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*<route>*namespace ''*");
    }

    [Fact]
    public void WrongRootNamespace_GetsAClearError()
    {
        var act = () => _context.AddXmlRoutesFromContent(
            """<routes xmlns="urn:someone:else"><route/></routes>""");

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*unexpected root namespace*urn:redb:route:1.0*");
    }

    [Fact]
    public void ErroredDocument_RegistersNothing()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="half-good">
                <from uri="direct://half-in"/>
                <removeBody/>
              </route>
              <route id="broken">
                <from uri="direct://broken-in"/>
                <nosuch/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>();
        _context.Start().GetAwaiter().GetResult();
        _context.Routes.Select(r => r.RouteId).Should().NotContain("half-good", "a document with errors registers none of its routes");
    }

    private static IExchange Msg(params (string Name, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message("x"));
        foreach (var (name, value) in headers) exchange.In.Headers[name] = value;
        return exchange;
    }
}
