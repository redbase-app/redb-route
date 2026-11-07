using System.Xml.Linq;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The step <c>&lt;bean ref|beanType method/&gt;</c> (Р8, amended 2026-11-05): the structured endpoint child of
/// <c>&lt;to&gt;</c> without the wrapper. It normalizes through the same path into the same <c>bean:</c> endpoint;
/// the name <c>bean</c> keeps its other two places (the declaration under <c>&lt;routes&gt;</c>/<c>&lt;context&gt;</c>
/// and the endpoint child of <c>&lt;to&gt;</c>) exactly as before.
/// </summary>
public class BeanStepXmlTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string Greeter => $"{typeof(XmlGreeter).FullName}, {typeof(XmlGreeter).Assembly.GetName().Name}";

    private string? StepUri(string stepXml, out List<string> errors)
    {
        var ctx = new XmlParseContext(_context, ElementRegistry.CreateDefault(), "probe.xml");
        var uri = StructuredEndpoint.ResolveBeanStep(XElement.Parse(stepXml, LoadOptions.SetLineInfo), ctx);
        errors = [.. ctx.Errors];
        return uri;
    }

    private async Task<IProducer> StartAndProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    private static string CodeOf(string xml)
        => XmlCodeGenerator.Generate(XDocument.Parse(xml), "Beans", "Tests.Generated", sourceName: "<inline>");

    // ── normalization ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""<bean ref="#greeter" method="Greet"/>""", "bean://#greeter?method=Greet")]
    [InlineData("""<bean ref="greeter" method="Greet"/>""", "bean://#greeter?method=Greet")]
    [InlineData("""<bean ref="#greeter"/>""", "bean://#greeter")]
    [InlineData("""<bean beanType="Acme.Pricing, Acme" method="Gross(${header.qty})" Prefix="x"/>""",
        "bean://Acme.Pricing, Acme?method=Gross(${header.qty})&Prefix=x")]
    [InlineData("""<bean id="call" description="d" ref="#greeter" method="Greet"/>""", "bean://#greeter?method=Greet")]
    public void Step_NormalizesIntoTheBeanUri(string step, string expected)
    {
        StepUri(step, out var errors).Should().Be(expected);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Step_AndTheToChild_MeetAtOneEndpointKey()
    {
        var step = StepUri("""<bean ref="#greeter" method="Greet"/>""", out _)!;
        var ctx = new XmlParseContext(_context, ElementRegistry.CreateDefault(), "probe.xml");
        var child = StructuredEndpoint.Resolve(
            XElement.Parse("""<to><bean path="#greeter" method="Greet"/></to>"""), ctx, out _)!;

        EndpointUriParser.Parse(step).NormalizedKey.Should().Be(EndpointUriParser.Parse(child).NormalizedKey);
        EndpointUriParser.Parse(step).NormalizedKey
            .Should().Be(EndpointUriParser.Parse("bean:#greeter?method=Greet").NormalizedKey);
    }

    [Theory]
    [InlineData("""<bean method="Greet"/>""", "exactly one of ref=")]
    [InlineData("""<bean ref="#a" beanType="A, B"/>""", "exactly one of ref=")]
    [InlineData("""<bean path="#greeter"/>""", "not path=")]
    [InlineData("""<bean name="greeter" type="A, B"/>""", "is a declaration")]
    [InlineData("""<bean ref="#greeter"><property key="Prefix" value="x"/></bean>""", "attributes only")]
    [InlineData("""<bean ref="#greeter?method=Greet"/>""", "contains '?'")]
    public void Step_Malformed_IsAPositionedError(string step, string message)
    {
        StepUri(step, out var errors).Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Contain(message);
    }

    // ── the three places of <bean> ───────────────────────────────────────────

    [Fact]
    public async Task AllThreePlaces_ParseAndRun_InOneDocument()
    {
        _context.AddXmlRoutesFromContent($$"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="greeter" type="{{Greeter}}">
                <property key="Prefix" value="decl"/>
              </bean>
              <route id="xml-bean-step">
                <from uri="direct://xml-bean-step"/>
                <bean ref="#greeter" method="Greet"/>
                <setHeader name="first" expr="${body}"/>
                <to uri="bean:#greeter?method=Greet"/>
                <filter expr="header.go == 'yes'">
                  <bean ref="#greeter" method="Greet"/>
                </filter>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://xml-bean-step");

        var exchange = new Exchange(new Message("bob"));
        exchange.In.Headers["go"] = "yes";
        await producer.Process(exchange);

        exchange.In.Headers["first"].Should().Be("decl:bob");
        exchange.In.Body.Should().Be("decl:decl:decl:bob", "the step, the bean: URI and the step in a scope each called it");
    }

    [Fact]
    public async Task BeanTypeStep_CreatesTheInstance_WithItsProperties()
    {
        _context.AddXmlRoutesFromContent($$"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-bean-type-step">
                <from uri="direct://xml-bean-type-step"/>
                <bean beanType="{{Greeter}}" method="Greet" Prefix="typed"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://xml-bean-type-step");

        var exchange = new Exchange(new Message("ann"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("typed:ann");
    }

    [Fact]
    public void StepAtTheRoutesLevel_IsReadAsADeclaration_AndRefused()
    {
        // At the container level a <bean> is a declaration: the step attributes do not make it a step.
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <bean ref="#greeter" method="Greet"/>
              <route id="xml-bean-wrong-place">
                <from uri="direct://xml-bean-wrong-place"/>
                <to uri="log:x"/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*is a step: it belongs inside a <route>*");
    }

    [Fact]
    public void DeclarationInsideARoute_IsRefused_PointingToTheContainerLevel()
    {
        var act = () => _context.AddXmlRoutesFromContent($$"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-bean-decl-in-route">
                <from uri="direct://xml-bean-decl-in-route"/>
                <bean name="greeter" type="{{Greeter}}"/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*is a declaration*<routes> or <context>*");
    }

    // ── schema ───────────────────────────────────────────────────────────────

    private static string ThreePlaces => $$"""
        <routes xmlns="urn:redb:route:1.0">
          <bean name="greeter" type="{{Greeter}}">
            <property key="Prefix" value="decl"/>
          </bean>
          <route id="schema-three-places">
            <from uri="direct://in"/>
            <bean ref="#greeter" method="Greet"/>
            <to><bean path="#greeter" method="Greet"/></to>
          </route>
        </routes>
        """;

    [Fact]
    public void CatalogSchema_TakesAllThreePlaces()
    {
        var catalog = ComponentCatalog.Build([new Components.Bean.BeanComponent()]);

        XmlRouteSchema.Validate(XDocument.Parse(ThreePlaces, LoadOptions.SetLineInfo),
                ElementRegistry.CreateDefault(), catalog)
            .Should().BeEmpty();
    }

    [Fact]
    public void CoreSchema_TakesAllThreePlaces()
    {
        XmlRouteSchema.Validate(XDocument.Parse(ThreePlaces, LoadOptions.SetLineInfo), ElementRegistry.CreateDefault())
            .Should().BeEmpty();
    }

    [Fact]
    public void Schema_TakesTheStep_AndKeepsTheDeclaration()
    {
        var problems = XmlRouteSchema.Validate(XDocument.Parse($$"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="greeter" type="{{Greeter}}">
                <property key="Prefix" value="decl"/>
              </bean>
              <route id="schema-bean-step">
                <from uri="direct://in"/>
                <bean ref="#greeter" method="Greet"/>
                <bean beanType="{{Greeter}}" method="Greet" Prefix="x"/>
                <filter expr="${header.go}">
                  <bean id="inner" ref="#greeter"/>
                </filter>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), ElementRegistry.CreateDefault());

        problems.Should().BeEmpty();
    }

    [Fact]
    public void Schema_RefusesADeclarationChildOnTheStep()
    {
        var problems = XmlRouteSchema.Validate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="schema-bean-step-child">
                <from uri="direct://in"/>
                <bean ref="#greeter"><property key="Prefix" value="x"/></bean>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), ElementRegistry.CreateDefault());

        problems.Should().NotBeEmpty("the step takes attributes only; <property> belongs to a declaration");
    }

    // ── code generation ──────────────────────────────────────────────────────

    [Fact]
    public void Print_IsTheBeanSugar_OrTheUriWhenTheStepSetsProperties()
    {
        var code = CodeOf($$"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="print-bean-step">
                <from uri="direct://in"/>
                <bean ref="#greeter" method="Greet"/>
                <bean ref="greeter"/>
                <bean beanType="{{Greeter}}" method="Greet" Prefix="x"/>
              </route>
            </routes>
            """);

        code.Should().Contain(".Bean(\"#greeter\", \"Greet\")");
        code.Should().Contain(".Bean(\"#greeter\")");
        code.Should().Contain($".To(\"bean://{Greeter}?method=Greet&Prefix=x\")");
    }

    [Fact]
    public void Print_ProducesTheSameUri_TheLoaderBuilds()
    {
        const string xml = """
            <routes xmlns="urn:redb:route:1.0">
              <route id="print-bean-same">
                <from uri="direct://print-bean-same"/>
                <bean ref="#greeter" method="Greet"/>
              </route>
            </routes>
            """;
        _context.AddXmlRoutesFromContent(xml);
        var builder = _context.RouteBuilders.Single();
        if (!builder.IsBuilt)
            builder.InternalBuild(_context);
        var loaded = builder.Definitions.Single().Outputs.OfType<ToDefinition>().Single().Uri;

        EndpointUriParser.Parse(loaded).NormalizedKey
            .Should().Be(EndpointUriParser.Parse(Components.Bean.BeanComponent.UriFor("#greeter", "Greet")).NormalizedKey);
        CodeOf(xml).Should().Contain(".Bean(\"#greeter\", \"Greet\")");
    }
}
