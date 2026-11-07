using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The structured endpoint child in the schema the loader validates against (no component catalog). The wildcard
/// used to be <c>processContents="lax"</c>, which validates a child against a global declaration of the same name:
/// <c>&lt;to&gt;&lt;bean path/&gt;&lt;/to&gt;</c> was read as the bean declaration and <c>&lt;to&gt;&lt;log/&gt;&lt;/to&gt;</c>
/// as the log step, and both were refused. It is <c>skip</c> now; the child is checked where the endpoint is bound.
/// The change may only accept more: every document the old schema accepted is still accepted.
/// </summary>
public class EndpointChildSchemaTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The root of the redb.Route tree: <c>redb.Route/</c> in the monorepo, the repository root of the public
    /// mirror. The test file sits two levels below it in both (<c>tests/redb.Route.Tests.Xml/</c>).
    /// </summary>
    private static string RouteTreeRoot([CallerFilePath] string file = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));

    /// <summary>
    /// Documents of the redb.Route tree alone, the set the public mirror also has: 231 here on 2026-11-05, of which
    /// at most 5 are docs examples the mirror does not ship.
    /// </summary>
    private const int RouteTreeDocumentsAtLeast = 200;

    /// <summary>The schema as it was: the same generated document with the endpoint wildcard back on lax.</summary>
    private static XmlSchemaSet PreviousCoreSchema()
    {
        var document = XmlRouteSchema.Generate(ElementRegistry.CreateDefault());
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        var wildcards = document.Descendants(xs + "any")
            .Where(a => (string?)a.Attribute("processContents") == "skip"
                        && (string?)a.Attribute("namespace") == "##targetNamespace")
            .ToList();
        wildcards.Should().NotBeEmpty("the no-catalog endpoint wildcard is the one this change touched");
        foreach (var wildcard in wildcards)
            wildcard.SetAttributeValue("processContents", "lax");
        var set = new XmlSchemaSet();
        using var reader = document.CreateReader();
        set.Add(XmlRouteLoader.Namespace, reader);
        set.Compile();
        return set;
    }

    private static string[] Problems(XDocument document, XmlSchemaSet schema)
        => [.. XmlRouteSchema.Validate(document, schema).Select(p => $"{p.Line}:{p.Column}: {p.Message}")];

    /// <summary>Step names and the bean declaration that a component of a default context shares.</summary>
    private string[] CollidingSchemes()
    {
        var elements = ElementRegistry.CreateDefault().Contributions.Select(c => c.Name).Append("bean").ToHashSet(StringComparer.Ordinal);
        return [.. _context.GetComponentNames().Where(elements.Contains).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void EveryComponentNamedLikeAnElement_LoadsAsAStructuredChild_WithoutACatalog()
    {
        var colliding = CollidingSchemes();
        colliding.Should().Contain(["bean", "log"], "the two reported collisions are among them");

        foreach (var scheme in colliding)
        {
            var document = XDocument.Parse($"""
                <routes xmlns="urn:redb:route:1.0">
                  <route id="collide-{scheme}">
                    <from uri="direct://collide-{scheme}"/>
                    <to><{scheme} path="#target" method="M"/></to>
                  </route>
                </routes>
                """, LoadOptions.SetLineInfo);

            Problems(document, XmlRouteSchema.CoreSet).Should().BeEmpty($"<to><{scheme}/></to> is an endpoint child");
            Problems(document, PreviousCoreSchema()).Should().NotBeEmpty(
                $"the lax wildcard validated <{scheme}> against the element of the same name");
        }
    }

    [Fact]
    public void BeanAndLogChildren_LoadThroughTheLoader()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="children-load">
                <from uri="direct://children-load"/>
                <to><log path="orders" level="INFO"/></to>
                <to><bean path="#greeter" method="Greet"/></to>
              </route>
            </routes>
            """);

        _context.RouteBuilders.Should().ContainSingle();
    }

    [Fact]
    public void EveryDocumentThePreviousSchemaAccepted_IsStillAccepted()
    {
        var previous = PreviousCoreSchema();
        var documents = FixtureDocuments().ToList();
        var routeTree = documents.Count(d => d.RouteTree);
        routeTree.Should().BeGreaterThanOrEqualTo(RouteTreeDocumentsAtLeast,
            "the redb.Route tree's own fixtures, present in the monorepo and in the public mirror alike");

        var acceptedBefore = 0;
        foreach (var (source, _, document) in documents)
        {
            var before = Problems(document, previous);
            var after = Problems(document, XmlRouteSchema.CoreSet);
            after.Should().BeSubsetOf(before, $"{source}: the change may only accept more");
            if (before.Length == 0)
                acceptedBefore++;
        }
        acceptedBefore.Should().BeGreaterThanOrEqualTo(RouteTreeDocumentsAtLeast / 2,
            "most fixtures are valid documents, so the subset check is not vacuous");
    }

    [Fact]
    public void MalformedChildShape_IsStillAPositionedLoadError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="child-shape">
                <from uri="direct://child-shape"/>
                <to><seda path="q"><nested><deeper/></nested></seda></to>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*(4,*must be a family entry*");
    }

    [Fact]
    public async Task UnknownOptionOnAStrictChild_IsRefusedWhereTheEndpointIsBound()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="child-option">
                <from uri="direct://child-option"/>
                <to><seda path="q" sizee="5"/></to>
              </route>
            </routes>
            """);

        var start = () => _context.Start();

        (await start.Should().ThrowAsync<Exception>())
            .Which.ToString().Should().Contain("'sizee' is not an option of the Seda endpoint");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static readonly Regex RawXml = new("\"\"\"\\s*(?<xml><(?:routes|context)\\b.*?)\"\"\"", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Every route document the products' tests and examples load. From the redb.Route tree: XML files of the
    /// examples, the demos (<c>demos/</c> here, <c>demo/</c> in the public mirror) and the Xml tests, and the
    /// raw-string documents in the Xml tests. Beside the tree, when the monorepo has them: the route-xml templates and
    /// the raw-string documents of the Tsak tests. Interpolated documents that do not parse as written are left out.
    /// </summary>
    private static IEnumerable<(string Source, bool RouteTree, XDocument Document)> FixtureDocuments()
    {
        var route = RouteTreeRoot();
        var beside = Path.GetDirectoryName(route)!;

        foreach (var (directory, routeTree) in new[]
                 {
                     (Path.Combine(route, "docs", "Route-XML", "examples"), true),
                     (Path.Combine(route, "demos"), true),
                     (Path.Combine(route, "demo"), true),
                     (Path.Combine(route, "tests", "redb.Route.Tests.Xml"), true),
                     (Path.Combine(beside, "redb.Templates", "templates", "route-xml"), false),
                 })
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories).Where(NotBuildOutput))
                if (TryParse(File.ReadAllText(file)) is { } document && IsRouteDocument(document))
                    yield return (Path.GetRelativePath(beside, file), routeTree, document);
        }

        foreach (var (directory, routeTree) in new[]
                 {
                     (Path.Combine(route, "tests", "redb.Route.Tests.Xml"), true),
                     (Path.Combine(beside, "redb.Tsak", "tests"), false),
                 })
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Where(NotBuildOutput))
            {
                var index = 0;
                foreach (Match match in RawXml.Matches(File.ReadAllText(file)))
                {
                    index++;
                    if (TryParse(match.Groups["xml"].Value) is { } document && IsRouteDocument(document))
                        yield return ($"{Path.GetRelativePath(beside, file)}#{index}", routeTree, document);
                }
            }
        }
    }

    private static bool NotBuildOutput(string file)
        => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool IsRouteDocument(XDocument document)
        => document.Root?.Name.NamespaceName == XmlRouteLoader.Namespace;

    private static XDocument? TryParse(string xml)
    {
        try
        {
            return XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
