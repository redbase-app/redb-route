using FluentAssertions;
using redb.Route.Core;
using redb.Route.Diagnostics;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml.Examples;

/// <summary>
/// Route-XML Ф6 §5 — the mechanical honesty check: the committed generated builders (compiled
/// as part of this project) produce definition trees BYTE-IDENTICAL to loading the source XML.
/// This is what catches a wrong overload, a lost attribute, an eaten branch or a swapped
/// argument in the generator.
/// </summary>
public class ExamplesGeneratedEquivalenceTests
{
    private static string DescribeXml(string fileName)
    {
        using var context = ExampleHarness.NewContext();
        context.AddXmlRoutes(Path.Combine(ExampleHarness.Dir, fileName));
        return RouteDescriber.Describe(ExampleHarness.Definitions(context));
    }

    private static string DescribeGenerated(RouteBuilder builder)
    {
        using var context = ExampleHarness.NewContext();
        context.AddRoutes(builder);
        return RouteDescriber.Describe(ExampleHarness.Definitions(context));
    }

    [Fact]
    public void ScopeDiag_GeneratedCode_IsTheSameTree()
        => DescribeGenerated(new Generated.ScopeDiagRoutes()).Should().Be(DescribeXml("scope-diag.route.xml"));

    [Fact]
    public void Eip_GeneratedCode_IsTheSameTree()
        => DescribeGenerated(new Generated.EipRoutes()).Should().Be(DescribeXml("eip.route.xml"));

    [Fact]
    public void DeepDslShowcase_GeneratedCode_IsTheSameTree()
        => DescribeGenerated(new Generated.DeepDslShowcaseRoutes()).Should().Be(DescribeXml("deep-dsl-showcase.route.xml"));

    [Fact]
    public void Factories_GeneratedCode_IsTheSameTree()
        => DescribeGenerated(new Generated.FactoriesRoutes()).Should().Be(DescribeXml("factories.route.xml"));

    [Fact]
    public void MainPipeline_GeneratedCode_IsTheSameTree()
        => DescribeGenerated(new Generated.MainPipelineRoutes()).Should().Be(DescribeXml("main-pipeline.route.xml"));
}
