using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml.Examples;

/// <summary>
/// Route-XML Ф6: every shipped example generates C# that is committed next to the tests
/// (Generated/*.cs), compiled as part of this project, and — in <c>ExamplesGeneratedEquivalenceTests</c> —
/// executed to prove the generated tree is byte-identical to the loaded XML. The committed file
/// is a golden: regenerate deliberately with REDB_XML_CODEGEN_REGEN=1, never to make a diff go
/// away.
/// </summary>
public class ExamplesCodeGenTests
{
    internal static string GeneratedDir { get; } = Path.Combine(
        Directory.GetParent(ExampleHarness.Dir)!.Parent!.Parent!.FullName,
        "tests", "redb.Route.Tests.Xml", "Examples", "Generated");

    internal static string ClassNameFor(string fileName)
    {
        var stem = Path.GetFileName(fileName).Replace(".route.xml", "", StringComparison.Ordinal);
        var parts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..])) + "Routes";
    }

    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(ExampleHarness.Dir, "*.route.xml").OrderBy(f => f, StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));
        return data;
    }

    private static string Generate(string fileName, CodeGenStyle style)
    {
        var path = Path.Combine(ExampleHarness.Dir, fileName);
        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
        return XmlCodeGenerator.Generate(document, ClassNameFor(fileName),
            "redb.Route.Tests.Xml.Examples.Generated", style, path);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void GeneratedCode_MatchesTheCommittedFile(string fileName)
    {
        var generated = Generate(fileName, CodeGenStyle.Readable)
            .Replace(ExampleHarness.Dir.Replace('\\', '/'), "<examples>", StringComparison.Ordinal);
        var committedPath = Path.Combine(GeneratedDir, ClassNameFor(fileName) + ".cs");

        if (Environment.GetEnvironmentVariable("REDB_XML_CODEGEN_REGEN") == "1")
        {
            Directory.CreateDirectory(GeneratedDir);
            File.WriteAllText(committedPath, generated);
            return;
        }

        File.Exists(committedPath).Should().BeTrue($"the generated {ClassNameFor(fileName)}.cs must be committed");
        File.ReadAllText(committedPath).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Should().Be(generated, "a diff means either an intended generator change (explain it) or a regression");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Generation_IsDeterministic(string fileName)
    {
        Generate(fileName, CodeGenStyle.Machine).Should().Be(Generate(fileName, CodeGenStyle.Machine));
    }

    [Fact]
    public void MachineStyle_MapsDiagnosticsBackToTheXml()
    {
        var machine = Generate("scope-diag.route.xml", CodeGenStyle.Machine);

        machine.Should().Contain("#line ").And.Contain("scope-diag.route.xml\"");
        machine.Should().EndWith("#line default\n");
        Generate("scope-diag.route.xml", CodeGenStyle.Readable).Should().NotContain("#line ");
    }
}
