using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>A bean the onInit pipeline calls; counts the calls.</summary>
public class InitProbe
{
    public int Marked { get; private set; }
    public void Mark(IExchange exchange) => Marked++;
}

/// <summary>Options object built as a nested anonymous bean.</summary>
public class ProbeOptions
{
    public string? ConnectionString { get; set; }
}

/// <summary>A bean taking an options object through the constructor.</summary>
public class ProbeFactory(ProbeOptions options)
{
    public string Describe(IExchange exchange) => $"factory:{options.ConnectionString}";
}

/// <summary>A minimal component for the <c>&lt;components&gt;</c> section test.</summary>
public sealed class ProbeComponent : ComponentBase
{
    public override string Scheme => "probe";
    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException("registration-only test component");
}

/// <summary>
/// Route-XML Ф2, context batch: <c>context.xml</c> — the components section, context beans with
/// nested constructor beans, the onInit pipeline in the fail-fast bootstrap phase, and the
/// {{...}}-aware <c>enabled=</c> on routes.
/// </summary>
public class XmlContextLoaderTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ── onInit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnInit_RunsOnce_BeforeTheRoutesStart()
    {
        var probe = new InitProbe();
        _context.AddToRegistry("init-probe", probe);
        _context.AddXmlContextFromContent("""
            <context xmlns="urn:redb:route:1.0">
              <onInit>
                <to uri="bean:#init-probe?method=Mark"/>
              </onInit>
            </context>
            """);

        probe.Marked.Should().Be(0, "the pipeline runs at Start(), not at load");
        await _context.Start();

        probe.Marked.Should().Be(1);
    }

    [Fact]
    public async Task OnInit_FailingStep_KeepsTheContextFromStarting()
    {
        _context.AddXmlContextFromContent("""
            <context xmlns="urn:redb:route:1.0">
              <onInit>
                <throwException type="System.InvalidOperationException" message="ddl failed"/>
              </onInit>
            </context>
            """, "broken-init.xml");

        var act = () => _context.Start();

        (await act.Should().ThrowAsync<AggregateException>())
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*<onInit> of 'broken-init.xml' failed*ddl failed*");
    }

    // ── components and beans ─────────────────────────────────────────────────

    [Fact]
    public void ComponentsSection_RegistersTheScheme_ForRouteFilesToUse()
    {
        _context.AddXmlContextFromContent($$"""
            <context xmlns="urn:redb:route:1.0">
              <components>
                <component type="{{typeof(ProbeComponent).FullName}}, {{typeof(ProbeComponent).Assembly.GetName().Name}}"/>
              </components>
            </context>
            """);

        _context.GetComponentNames().Should().Contain("probe");
        // The scheme check of a routes document now accepts it (endpoints stay lazy).
        var load = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ctx-probe">
                <from uri="direct://ctx-probe-in"/>
                <to><probe path="anywhere"/></to>
              </route>
            </routes>
            """);
        load.Should().NotThrow();
    }

    [Fact]
    public async Task ConstructorArg_NestedAnonymousBean_BuildsTheGraph()
    {
        _context.AddXmlContextFromContent($$"""
            <context xmlns="urn:redb:route:1.0">
              <bean name="probe-factory" type="{{typeof(ProbeFactory).FullName}}, {{typeof(ProbeFactory).Assembly.GetName().Name}}">
                <constructorArg>
                  <bean type="{{typeof(ProbeOptions).FullName}}, {{typeof(ProbeOptions).Assembly.GetName().Name}}">
                    <property key="ConnectionString" value="Host=demo"/>
                  </bean>
                </constructorArg>
              </bean>
            </context>
            """);
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ctx-factory">
                <from uri="direct://ctx-factory-in"/>
                <to uri="bean:#probe-factory?method=Describe"/>
              </route>
            </routes>
            """);
        await _context.Start();
        var producer = _context.GetEndpoint("direct://ctx-factory-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("factory:Host=demo");
    }

    [Fact]
    public void ConstructorArg_ValueAndNestedBean_Together_IsASchemaError()
    {
        var act = () => _context.AddXmlContextFromContent($$"""
            <context xmlns="urn:redb:route:1.0">
              <bean name="bad" type="{{typeof(ProbeFactory).FullName}}, {{typeof(ProbeFactory).Assembly.GetName().Name}}">
                <constructorArg value="x">
                  <bean type="{{typeof(ProbeOptions).FullName}}, {{typeof(ProbeOptions).Assembly.GetName().Name}}"/>
                </constructorArg>
              </bean>
            </context>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*either value= or exactly one nested anonymous <bean*");
    }

    [Fact]
    public void UnknownContextSection_And_SecondOnInit_AreCollectedTogether()
    {
        var act = () => _context.AddXmlContextFromContent("""
            <context xmlns="urn:redb:route:1.0">
              <onInit><removeBody/></onInit>
              <onInit><removeBody/></onInit>
              <routes/>
            </context>
            """, "bad-context.xml");

        var errors = act.Should().Throw<XmlRouteException>().Which.Errors;
        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.Contains("at most one <onInit>"));
        errors.Should().Contain(e => e.Contains("unknown <context> section <routes>"));
    }

    // ── {{...}}-aware enabled= ───────────────────────────────────────────────

    [Fact]
    public async Task EnabledPlaceholder_ResolvesFromContextProperties_AndDefaults()
    {
        _context.SetProperty("features.on", "true");
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="en-on" enabled="{{features.on}}">
                <from uri="direct://en-on-in"/><removeBody/>
              </route>
              <route id="en-default-off" enabled="{{features.off:false}}">
                <from uri="direct://en-off-in"/><removeBody/>
              </route>
            </routes>
            """);
        await _context.Start();

        var ids = _context.Routes.Select(r => r.RouteId).ToList();
        ids.Should().Contain("en-on").And.NotContain("en-default-off");
    }

    [Fact]
    public void EnabledPlaceholder_WithoutValueOrDefault_IsAPositionedError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="en-missing" enabled="{{no.such.key}}">
                <from uri="direct://en-missing-in"/><removeBody/>
              </route>
            </routes>
            """, "enabled.xml");

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*enabled.xml(2,*Unresolved property placeholder*no.such.key*");
    }

    [Fact]
    public void EnabledResolvingToNonBool_IsASchemaError()
    {
        _context.SetProperty("features.kind", "sometimes");
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="en-nonbool" enabled="{{features.kind}}">
                <from uri="direct://en-nonbool-in"/><removeBody/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*resolved to 'sometimes', which is not a bool*");
    }
}
