using System.Xml;
using System.Xml.Linq;

namespace redb.Route.Xml.Packaging;

/// <summary>
/// A check a package of the route format runs over a route project at <c>check</c> and <c>pack</c> time, beside the
/// gate's own. A connector or data format implements it where its exact rules need its own code (the xmljson format
/// compiles the schema with its own reader and walks it). With <c>--bin</c> the tool creates every public, non-abstract
/// implementation with a parameterless constructor in the <c>redb.Route.*.dll</c> of the build output, the same way it
/// finds element contributions; without <c>--bin</c> none runs.
/// </summary>
public interface IPackageCheck
{
    /// <summary>Examines the project and reports its findings through <paramref name="context"/>.</summary>
    void Check(PackageCheckContext context);
}

/// <summary>One route document of the project, loaded with line information.</summary>
/// <param name="File">The path the findings name, relative to the project.</param>
/// <param name="Document">The document; every node carries its line and position.</param>
public sealed record PackageDocument(string File, XDocument Document);

/// <summary>What a <see cref="IPackageCheck"/> sees of the project, and where it reports.</summary>
public sealed class PackageCheckContext
{
    private readonly List<PackageFinding> _findings;

    internal PackageCheckContext(string projectDirectory, IReadOnlyList<PackageDocument> documents, List<PackageFinding> findings)
    {
        ProjectDirectory = projectDirectory;
        Documents = documents;
        _findings = findings;
    }

    /// <summary>The project directory.</summary>
    public string ProjectDirectory { get; }

    /// <summary>The route documents of the project.</summary>
    public IReadOnlyList<PackageDocument> Documents { get; }

    /// <summary>
    /// The full path of a resource a route refers to, held to what the package ships (<c>resources/</c>), as the gate
    /// holds its own resource references; <c>null</c> with the reason when it is missing or outside <c>resources/</c>.
    /// </summary>
    /// <param name="reference">The reference as written (<c>order.xsd</c>, <c>resources/schemas/order.xsd</c>).</param>
    /// <param name="problem">The reason, worded to follow the reference (<c>is not under 'resources/'</c>).</param>
    public string? Resource(string reference, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return RoutePackage.PackagedResource(ProjectDirectory, reference, out problem);
    }

    /// <summary>An error at a node of a document: the package does not build.</summary>
    public void Error(PackageDocument document, XObject at, string message) => Add(true, document, at, message);

    /// <summary>A warning at a node of a document.</summary>
    public void Warning(PackageDocument document, XObject at, string message) => Add(false, document, at, message);

    private void Add(bool error, PackageDocument document, XObject at, string message)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(at);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var line = (IXmlLineInfo)at;
        _findings.Add(new PackageFinding(error, $"{document.File}({line.LineNumber},{line.LinePosition})", message));
    }
}
