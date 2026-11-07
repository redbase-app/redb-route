using System.Xml.Linq;
using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>A C# route builder a context.xml names: answers on direct://rb-cs with the body it got, marked.</summary>
public sealed class MarkupNamedRoute : RouteBuilder
{
    protected override void Configure()
    {
        From("direct://rb-cs").RouteId("cs-orders")
            .Process(e => e.In.Body = $"cs:{e.In.Body}");
    }
}

/// <summary>Not a candidate: abstract.</summary>
public abstract class AbstractMarkupRoute : RouteBuilder;

/// <summary>Not a candidate: a builder that needs a constructor argument.</summary>
public sealed class ArgumentMarkupRoute(string name) : RouteBuilder
{
    protected override void Configure() => From("direct://" + name).Log("x");
}

/// <summary>Not a candidate: not public.</summary>
internal sealed class HiddenMarkupRoute : RouteBuilder
{
    protected override void Configure() => From("direct://hidden").Log("x");
}

/// <summary>Not a candidate: not a route builder at all.</summary>
public sealed class NotARouteBuilder;

/// <summary>
/// <c>&lt;routeBuilder type="…"/&gt;</c> in <c>context.xml</c> adds a C# route builder to the context, so one package carries
/// routes in markup and in the DSL, side by side in one context. The type follows the rule of <c>AddRoutes(Assembly[])</c>,
/// a wrong one is a load error with its position and, with the built assemblies at hand, a packaging error.
/// </summary>
public sealed class RouteBuilderContextTests : IAsyncLifetime, IDisposable
{
    private readonly RouteContext _context = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-rb-" + Guid.NewGuid().ToString("N"));

    public RouteBuilderContextTests() => Directory.CreateDirectory(_root);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string TypeName<T>() => $"{typeof(T).FullName}, {typeof(T).Assembly.GetName().Name}";

    private static string Context(string inner) => $"""
        <context xmlns="urn:redb:route:1.0">
          {inner}
        </context>
        """;

    private void LoadContext(string inner)
        => new XmlContextLoader(_context).Load(XDocument.Parse(Context(inner), LoadOptions.SetLineInfo), "context.xml");

    [Fact]
    public async Task A_named_builder_runs_its_routes_next_to_the_markup_routes_in_one_context()
    {
        LoadContext($"""<routeBuilder type="{TypeName<MarkupNamedRoute>()}"/>""");
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-orders">
                <from uri="direct://rb-xml"/>
                <setBody expr="xml:${body}"/>
              </route>
            </routes>
            """);
        await _context.Start();
        await using var producer = new ProducerTemplate(_context);
        producer.Start();

        var fromCode = await producer.RequestBody("direct://rb-cs", "order");
        var fromMarkup = await producer.RequestBody("direct://rb-xml", "order");

        fromCode.Should().Be("cs:order");
        fromMarkup.Should().Be("xml:order");
        _context.Routes.Select(r => r.RouteId).Should().BeEquivalentTo(["cs-orders", "xml-orders"]);
    }

    [Theory]
    [InlineData(typeof(AbstractMarkupRoute), "is abstract")]
    [InlineData(typeof(ArgumentMarkupRoute), "has no public parameterless constructor")]
    [InlineData(typeof(HiddenMarkupRoute), "is not public")]
    [InlineData(typeof(NotARouteBuilder), "does not derive from redb.Route.Core.RouteBuilder")]
    public void A_type_the_rule_refuses_is_a_load_error_with_its_position(Type type, string reason)
    {
        var load = () => LoadContext($"""<routeBuilder type="{type.FullName}, {type.Assembly.GetName().Name}"/>""");

        load.Should().Throw<XmlRouteException>().WithMessage($"*context.xml(2,*<routeBuilder>*{reason}*");
        _context.RouteBuilders.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_type_is_a_load_error()
    {
        var load = () => LoadContext("""<routeBuilder type="Nowhere.Route, Nowhere"/>""");

        load.Should().Throw<XmlRouteException>().WithMessage("*context.xml(2,*'Nowhere.Route, Nowhere' was not found*");
    }

    [Fact]
    public void A_missing_type_is_a_load_error()
    {
        var load = () => LoadContext("<routeBuilder/>");

        load.Should().Throw<XmlRouteException>().WithMessage("*context.xml(2,*type*");
    }

    [Fact]
    public void A_document_with_an_error_further_down_adds_no_builder()
    {
        var load = () => LoadContext($"""
            <routeBuilder type="{TypeName<MarkupNamedRoute>()}"/>
              <routeBuilder type="Nowhere.Route, Nowhere"/>
            """);

        load.Should().Throw<XmlRouteException>();
        _context.RouteBuilders.Should().BeEmpty("a context.xml with errors must not leave routes running");
    }

    [Fact]
    public void The_same_builder_named_twice_is_a_load_error()
    {
        var load = () => LoadContext($"""
            <routeBuilder type="{TypeName<MarkupNamedRoute>()}"/>
              <routeBuilder type="{TypeName<MarkupNamedRoute>()}"/>
            """);

        load.Should().Throw<XmlRouteException>().WithMessage("*context.xml(3,*is already added to this context*");
    }

    [Fact]
    public void A_builder_already_added_in_code_is_a_load_error()
    {
        _context.AddRoutes(new MarkupNamedRoute());

        var load = () => LoadContext($"""<routeBuilder type="{TypeName<MarkupNamedRoute>()}"/>""");

        load.Should().Throw<XmlRouteException>().WithMessage("*is already added to this context*");
    }

    [Fact]
    public void Inside_a_route_it_is_refused()
    {
        var load = () => _context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="r">
                <from uri="direct://in"/>
                <routeBuilder type="{TypeName<MarkupNamedRoute>()}"/>
              </route>
            </routes>
            """);

        load.Should().Throw<XmlRouteException>().WithMessage("*routeBuilder*");
    }

    // ── the packaging check ─────────────────────────────────────────────────

    private string Project(string context)
    {
        var dir = RouteProjectScaffold.Create(_root, "Rb" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, RoutePackage.ContextFile), context);
        return dir;
    }

    private static Type? Resolve(string typeName) => Type.GetType(typeName);

    [Fact]
    public void The_check_passes_a_builder_the_loader_accepts()
    {
        var dir = Project(Context($"""<routeBuilder type="{TypeName<MarkupNamedRoute>()}"/>"""));

        var result = RoutePackage.Check(dir, "p", "1.0.0", Resolve);

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void The_check_fails_on_a_type_the_built_assemblies_lack()
    {
        var dir = Project(Context("""<routeBuilder type="Nowhere.Route, Nowhere"/>"""));

        var result = RoutePackage.Check(dir, "p", "1.0.0", Resolve);

        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("'Nowhere.Route, Nowhere' was not found in the built assemblies");
    }

    [Theory]
    [InlineData(typeof(NotARouteBuilder), "does not derive from redb.Route.Core.RouteBuilder")]
    [InlineData(typeof(ArgumentMarkupRoute), "has no public parameterless constructor")]
    [InlineData(typeof(AbstractMarkupRoute), "is abstract")]
    public void The_check_fails_on_a_type_the_loader_refuses(Type type, string reason)
    {
        var dir = Project(Context($"""<routeBuilder type="{type.FullName}, {type.Assembly.GetName().Name}"/>"""));

        var result = RoutePackage.Check(dir, "p", "1.0.0", Resolve);

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain(reason);
    }

    [Fact]
    public void The_check_fails_on_a_builder_without_a_type_even_without_the_built_assemblies()
    {
        var dir = Project(Context("<routeBuilder/>"));

        var result = RoutePackage.Check(dir, "p", "1.0.0");

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("<routeBuilder> needs type=");
    }
}
