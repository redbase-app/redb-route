using System.Xml.Linq;
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

    /// <summary>The creation path of a type that hides construction behind a static method.</summary>
    public static ProbeOptions FromDsn(string dsn) => new() { ConnectionString = dsn + "/via-factory" };
}

/// <summary>A bean whose OPTIONS come through a property, not the constructor: the shape a
/// connector factory has when it holds an object (a certificate, credentials, a serializer).</summary>
public class ProbeHolder
{
    public ProbeOptions? Options { get; set; }
    public string Describe(IExchange exchange) => $"holder:{Options?.ConnectionString}";
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
    public async Task Property_NestedAnonymousBean_IsAssignedAsTheObject()
    {
        // A property whose type is an OBJECT could not be set before: <property> took a string only,
        // so a factory holding a certificate or a credentials object forced the C# spelling.
        _context.AddXmlContextFromContent($$"""
            <context xmlns="urn:redb:route:1.0">
              <bean name="probe-holder" type="{{typeof(ProbeHolder).FullName}}, {{typeof(ProbeHolder).Assembly.GetName().Name}}">
                <property key="Options">
                  <bean type="{{typeof(ProbeOptions).FullName}}, {{typeof(ProbeOptions).Assembly.GetName().Name}}">
                    <property key="ConnectionString" value="Host=demo"/>
                  </bean>
                </property>
              </bean>
            </context>
            """);
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ctx-holder">
                <from uri="direct://ctx-holder-in"/>
                <to uri="bean:#probe-holder?method=Describe"/>
              </route>
            </routes>
            """);
        await _context.Start();
        var producer = _context.GetEndpoint("direct://ctx-holder-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("holder:Host=demo");
    }

    [Fact]
    public async Task Bean_FactoryMethod_CreatesThroughTheStaticMethod()
    {
        // Some types are created by a static method, not a public constructor (X509CertificateLoader
        // is the reason this exists). factoryMethod= names it; constructorArg values are its arguments.
        _context.AddXmlContextFromContent($$"""
            <context xmlns="urn:redb:route:1.0">
              <bean name="probe-factory" type="{{typeof(ProbeFactory).FullName}}, {{typeof(ProbeFactory).Assembly.GetName().Name}}">
                <constructorArg>
                  <bean type="{{typeof(ProbeOptions).FullName}}, {{typeof(ProbeOptions).Assembly.GetName().Name}}"
                        factoryMethod="FromDsn">
                    <constructorArg value="Host=demo"/>
                  </bean>
                </constructorArg>
              </bean>
            </context>
            """);
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ctx-static-factory">
                <from uri="direct://ctx-static-in"/>
                <to uri="bean:#probe-factory?method=Describe"/>
              </route>
            </routes>
            """);
        await _context.Start();
        var producer = _context.GetEndpoint("direct://ctx-static-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("factory:Host=demo/via-factory");
    }

    [Fact]
    public void StructuredEndpoint_CarriesAnExplicitFalse_AndOmitsAnAbsentOption()
    {
        // transacted on a producer is tri-state now (119e76d7): absent waits for the enclosing
        // transaction, "false" sends at once. The structured form must keep the two apart: an
        // absent attribute stays absent in the URI, an explicit false reaches it verbatim.
        var document = XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="gen-transacted">
                <from uri="direct://gen-transacted-in"/>
                <to><kafka path="orders" transacted="false"/></to>
                <to><kafka path="audit"/></to>
              </route>
            </routes>
            """);

        var code = XmlCodeGenerator.Generate(document, "TransactedGenerated", "Tests.Generated");

        code.Should().Contain("\"kafka://orders?transacted=false\"");
        code.Should().Contain("\"kafka://audit\"", "an attribute that is not written is not an option");
    }

    [Fact]
    public void Generator_PrintsTheObjectProperty_AndTheFactoryMethod()
    {
        // The C# spelling of the same two forms: a nested bean as the property value, and the
        // static creator as the last argument of XmlBeans.Create.
        var document = XDocument.Parse($$"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="holder" type="{{typeof(ProbeHolder).FullName}}, {{typeof(ProbeHolder).Assembly.GetName().Name}}">
                <property key="Options">
                  <bean type="{{typeof(ProbeOptions).FullName}}, {{typeof(ProbeOptions).Assembly.GetName().Name}}"
                        factoryMethod="FromDsn">
                    <constructorArg value="Host=demo"/>
                  </bean>
                </property>
              </bean>
              <route id="gen-bean">
                <from uri="direct://gen-bean-in"/>
                <to uri="bean:#holder?method=Describe"/>
              </route>
            </routes>
            """);

        var code = XmlCodeGenerator.Generate(document, "BeanGenerated", "Tests.Generated");

        code.Should().Contain("(\"Options\", XmlBeans.Create(")
            .And.Contain("\"FromDsn\")", "the factory method is the last argument");
    }

    [Fact]
    public void Property_ValueAndNestedBean_Together_IsASchemaError()
    {
        var act = () => _context.AddXmlContextFromContent($$"""
            <context xmlns="urn:redb:route:1.0">
              <bean name="bad" type="{{typeof(ProbeHolder).FullName}}, {{typeof(ProbeHolder).Assembly.GetName().Name}}">
                <property key="Options" value="x">
                  <bean type="{{typeof(ProbeOptions).FullName}}, {{typeof(ProbeOptions).Assembly.GetName().Name}}"/>
                </property>
              </bean>
            </context>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*either value= or exactly one nested anonymous <bean*");
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
