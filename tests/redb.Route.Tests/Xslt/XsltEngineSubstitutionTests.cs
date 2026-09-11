using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xslt;

namespace redb.Route.Tests.Xslt;

/// <summary>
/// <see cref="IXsltEngine"/> has been an interface since 3.5.0, and its documentation said another
/// engine could be plugged in. It could not: the DSL verbs and the component each named
/// <see cref="XslCompiledTransformEngine"/> directly, so there was no seam behind the interface.
/// <para>
/// Substitution now goes through <see cref="IXsltEngineFactory"/> — the factory rather than the
/// engine, because a stylesheet has to be compiled, and compiling is exactly what a different
/// processor does differently. These tests cover all three places a stylesheet gets compiled, plus
/// the default staying the default.
/// </para>
/// </summary>
public class XsltEngineSubstitutionTests : IAsyncDisposable
{
    private const string Stylesheet =
        """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" omit-xml-declaration="yes"/>
          <xsl:template match="/greeting"><hello><xsl:value-of select="name"/></hello></xsl:template>
        </xsl:stylesheet>
        """;

    private const string Input = "<greeting><name>world</name></greeting>";
    private const string BclResult = "<hello>world</hello>";

    private readonly string _stylesheetFile;

    public XsltEngineSubstitutionTests()
    {
        _stylesheetFile = Path.Combine(Path.GetTempPath(), $"redb-xslt-sub-{Guid.NewGuid():N}.xsl");
        File.WriteAllText(_stylesheetFile, Stylesheet);
    }

    public ValueTask DisposeAsync()
    {
        try { File.Delete(_stylesheetFile); } catch (IOException) { /* the temp file outlives the test at worst */ }
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // ── A stand-in processor ──

    /// <summary>An engine that ignores the stylesheet and reports what it was handed.</summary>
    private sealed class StubEngine(string origin) : IXsltEngine
    {
        public object Transform(object? body, XsltOutput output, IReadOnlyDictionary<string, object?>? parameters)
            => $"stub[{origin}]:{body}";
    }

    private sealed class StubFactory : IXsltEngineFactory
    {
        public int FileCompilations { get; private set; }
        public int ContentCompilations { get; private set; }

        public IXsltEngine FromFile(string stylesheetPath)
        {
            FileCompilations++;
            return new StubEngine("file");
        }

        public IXsltEngine FromContent(string stylesheetXml)
        {
            ContentCompilations++;
            return new StubEngine("content");
        }
    }

    // ── Harness ──

    private static async Task<object?> RunRoute(RouteContext context, string from, Action<IRouteDefinition> configure)
    {
        object? result = null;
        context.AddRoutes(r => configure(r.From(from).Process(_ => { })));

        await context.Start();
        var producer = context.GetEndpoint(from).CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(Input));
        await producer.Process(exchange);
        result = exchange.In.Body;
        return result;
    }

    // ── The default is unchanged ──

    [Fact]
    public async Task Without_a_registration_the_built_in_engine_still_runs()
    {
        await using var context = new RouteContext();

        var result = await RunRoute(context, "direct://xslt-default", r => r.XsltContent(Stylesheet));

        result.Should().Be(BclResult);
    }

    [Fact]
    public void The_default_factory_is_the_answer_when_nothing_is_registered()
    {
        var context = new RouteContext();

        context.GetXsltEngineFactory().Should().BeSameAs(XslCompiledTransformEngineFactory.Instance);
    }

    // ── Substitution reaches every place a stylesheet is compiled ──

    [Fact]
    public async Task A_registered_factory_compiles_the_inline_stylesheet_verb()
    {
        var factory = new StubFactory();
        await using var context = new RouteContext();
        context.UseXsltEngine(factory);

        var result = await RunRoute(context, "direct://xslt-sub-content", r => r.XsltContent(Stylesheet));

        result.Should().Be($"stub[content]:{Input}");
        factory.ContentCompilations.Should().Be(1);
    }

    [Fact]
    public async Task A_registered_factory_compiles_the_file_stylesheet_verb()
    {
        var factory = new StubFactory();
        await using var context = new RouteContext();
        context.UseXsltEngine(factory);

        var result = await RunRoute(context, "direct://xslt-sub-file", r => r.Xslt(_stylesheetFile));

        result.Should().Be($"stub[file]:{Input}");
        factory.FileCompilations.Should().Be(1);
    }

    [Fact]
    public async Task A_registered_factory_compiles_the_component_stylesheet()
    {
        var factory = new StubFactory();
        await using var context = new RouteContext();
        context.UseXsltEngine(factory);

        var result = await RunRoute(context, "direct://xslt-sub-comp", r => r.To($"xslt:{_stylesheetFile}"));

        result.Should().Be($"stub[file]:{Input}");
        factory.FileCompilations.Should().Be(1);
    }

    // ── Both registration surfaces ──

    [Fact]
    public void A_factory_registered_through_DI_is_found_too()
    {
        var factory = new StubFactory();
        var services = new ServiceCollection();
        services.AddXsltEngine(factory);

        var context = new RouteContext(services.BuildServiceProvider());

        context.GetXsltEngineFactory().Should().BeSameAs(factory);
    }

    [Fact]
    public void What_the_context_was_told_wins_over_the_container()
    {
        var fromContainer = new StubFactory();
        var fromContext = new StubFactory();
        var services = new ServiceCollection();
        services.AddXsltEngine(fromContainer);

        var context = new RouteContext(services.BuildServiceProvider());
        context.UseXsltEngine(fromContext);

        context.GetXsltEngineFactory().Should().BeSameAs(fromContext,
            "the order is the same as for every other pluggable service here");
    }
}
