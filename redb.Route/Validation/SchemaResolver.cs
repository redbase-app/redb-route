using System.Xml;
using System.Xml.Schema;

namespace redb.Route.Validation;

/// <summary>
/// Resolves the imports and includes of a schema to files under one directory, the package: never the network,
/// never a file outside it. A reference that leaves is an error naming it. One resolver for every caller that reads a
/// schema file (<c>validateXsd</c>, the xmljson data format); the callers differ only by the name in the messages.
/// </summary>
internal sealed class SchemaResolver(string caller, string root) : XmlUrlResolver
{
    private readonly string _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;

    /// <summary>
    /// The first reference refused. <see cref="XmlSchemaSet"/> reports a failed import only as a warning, so the refusal
    /// is kept here and raised once the set is compiled.
    /// </summary>
    public XmlSchemaException? Refusal { get; private set; }

    public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
    {
        if (!absoluteUri.IsFile)
            throw Refuse($"{caller}: the schema refers to '{absoluteUri}'; a schema and its imports are read from the package only, not the network.");
        var path = Path.GetFullPath(absoluteUri.LocalPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(_root, comparison))
            throw Refuse($"{caller}: the schema refers to '{path}', outside '{_root}'; imports and includes stay inside the package.");
        return base.GetEntity(absoluteUri, role, ofObjectToReturn);
    }

    private XmlSchemaException Refuse(string message)
    {
        var refusal = new XmlSchemaException(message);
        Refusal ??= refusal;
        return refusal;
    }

    /// <summary>
    /// Reads and compiles the schema at <paramref name="path"/>, its imports resolved under <paramref name="root"/>
    /// (relative to the schema's directory; the directory itself when <c>null</c>). Any problem the compiler reports, an
    /// unresolved import included, is an error: a schema read in part would validate or convert the wrong way without
    /// saying so.
    /// <para>
    /// A schema's own DOCTYPE is skipped (W3C's xmldsig-core-schema.xsd, imported by WS-Security, starts with one):
    /// nothing in it is expanded or fetched, so an entity reference in a schema's content fails as undeclared. Message
    /// documents stay strict: one with a DTD is refused (<see cref="redb.Route.Core.SafeXml"/>).
    /// </para>
    /// </summary>
    /// <param name="caller">The name the messages start with (<c>xmljson</c>, <c>validateXsd</c>).</param>
    /// <param name="path">The schema file.</param>
    /// <param name="root">The directory imports and includes must stay under, relative to the schema's directory.</param>
    /// <param name="targetNamespace">The schema's target namespace; <c>null</c> takes the schema's own.</param>
    public static XmlSchemaSet Load(string caller, string path, string? root, string? targetNamespace = null)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var resolver = new SchemaResolver(caller, root is null ? directory : Path.GetFullPath(Path.Combine(directory, root)));
        var set = new XmlSchemaSet { XmlResolver = resolver };
        set.ValidationEventHandler += (_, e) =>
        {
            if (resolver.Refusal is { } refusal)
                throw refusal;
            throw new XmlSchemaException($"{caller}: schema '{full}': {e.Message}", e.Exception);
        };
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = resolver };
        using (var reader = XmlReader.Create(full, settings))
            set.Add(targetNamespace, reader);
        set.Compile();
        if (resolver.Refusal is { } late)
            throw late;
        return set;
    }
}
