using FluentAssertions;
using redb.Route.Diagnostics;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml.Examples;

/// <summary>
/// Route-XML Ф3 §4: every shipped example loads through the real loader (stub schemes, mapped
/// bean types, the examples directory as the resource root) and its description matches the
/// committed golden file. A golden is NEVER updated to make a test green: a diff means either
/// an intended semantic change (explained in the commit) or a parser regression. Regenerate
/// deliberately with REDB_XML_GOLDEN_REGEN=1.
/// </summary>
public class ExamplesGoldenTests
{
    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(ExampleHarness.Dir, "*.route.xml").OrderBy(f => f, StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task Example_LoadsAndMatchesItsGolden(string fileName)
    {
        var path = Path.Combine(ExampleHarness.Dir, fileName);
        await using var context = ExampleHarness.NewContext();
        context.AddXmlRoutes(path);

        var described = RouteDescriber.Describe(ExampleHarness.Definitions(context));
        var goldenPath = Path.ChangeExtension(path, null) + ".expected.txt"; // x.route.xml → x.route.expected.txt

        if (Environment.GetEnvironmentVariable("REDB_XML_GOLDEN_REGEN") == "1")
        {
            File.WriteAllText(goldenPath, described);
            return;
        }

        File.Exists(goldenPath).Should().BeTrue($"the golden {Path.GetFileName(goldenPath)} must be committed next to the example");
        var golden = File.ReadAllText(goldenPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        described.Should().Be(golden);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task Example_RendersItsMermaidDiagram(string fileName)
    {
        var path = Path.Combine(ExampleHarness.Dir, fileName);
        await using var context = ExampleHarness.NewContext();
        context.AddXmlRoutes(path);

        var diagram = Route.Diagnostics.MermaidRenderer.Render(ExampleHarness.Definitions(context));
        diagram.Should().StartWith("flowchart TD",
            "top-down is the default: Mermaid cannot wrap a long chain, vertical scrolls like text");
        var goldenPath = Path.ChangeExtension(path, null) + ".mmd"; // x.route.xml → x.route.mmd

        if (Environment.GetEnvironmentVariable("REDB_XML_GOLDEN_REGEN") == "1")
        {
            File.WriteAllText(goldenPath, diagram);
            return;
        }

        File.Exists(goldenPath).Should().BeTrue($"the diagram {Path.GetFileName(goldenPath)} must be committed next to the example");
        File.ReadAllText(goldenPath).Replace("\r\n", "\n", StringComparison.Ordinal).Should().Be(diagram);
    }
}
