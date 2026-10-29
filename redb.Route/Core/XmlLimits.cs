namespace redb.Route.Core;

/// <summary>
/// Structure limits for XML read through <see cref="SafeXml"/>: how deep, how wide and how large a document may be.
/// The reader checks them while it reads, so a document over a limit is refused before it is built, and whatever
/// comes after the read (canonicalization, signature checks, XPath, XSLT, a deserializer) only ever sees a bounded
/// document, whatever version of the XML libraries the application runs on.
/// <para>
/// A null limit is not checked. <see cref="Default"/> bounds the structure only, generously enough that no ordinary
/// document meets it; a receiver that knows the shape of what it reads (an AS4 envelope) sets tighter ones.
/// </para>
/// </summary>
public sealed record XmlLimits
{
    /// <summary>Default <see cref="MaxDepth"/>: 256 levels of nested elements.</summary>
    public const int DefaultMaxDepth = 256;

    /// <summary>Default <see cref="MaxAttributesPerElement"/>: 1024, namespace declarations included.</summary>
    public const int DefaultMaxAttributesPerElement = 1024;

    /// <summary>Default <see cref="MaxNamespaceDeclarationsPerElement"/>: 256.</summary>
    public const int DefaultMaxNamespaceDeclarationsPerElement = 256;

    /// <summary>
    /// The limits every <see cref="SafeXml"/> loader applies when the caller names none: depth, attributes and
    /// namespace declarations per element. Size and element count are not bounded here; the caller that knows a sane
    /// size passes one.
    /// </summary>
    public static XmlLimits Default { get; } = new();

    /// <summary>
    /// Nesting depth of elements, the root counting as 1. Deep nesting is what recursive processing of a document
    /// (canonicalization, XPath, a serializer) pays for with its stack. Default <see cref="DefaultMaxDepth"/>.
    /// </summary>
    public int? MaxDepth { get; init; } = DefaultMaxDepth;

    /// <summary>
    /// Attributes on one element, namespace declarations included. Default <see cref="DefaultMaxAttributesPerElement"/>.
    /// </summary>
    public int? MaxAttributesPerElement { get; init; } = DefaultMaxAttributesPerElement;

    /// <summary>
    /// Namespace declarations (<c>xmlns</c>, <c>xmlns:p</c>) on one element: canonicalization works through the
    /// namespaces in scope of every element. Default <see cref="DefaultMaxNamespaceDeclarationsPerElement"/>.
    /// </summary>
    public int? MaxNamespaceDeclarationsPerElement { get; init; } = DefaultMaxNamespaceDeclarationsPerElement;

    /// <summary>Namespace declarations in the whole document. Default: not bounded.</summary>
    public long? MaxNamespaceDeclarations { get; init; }

    /// <summary>Elements in the whole document. Default: not bounded.</summary>
    public long? MaxElements { get; init; }

    /// <summary>
    /// Characters in the whole document, after entity references are resolved (<see cref="System.Xml.XmlReaderSettings.MaxCharactersInDocument"/>).
    /// Default: not bounded.
    /// </summary>
    public long? MaxCharacters { get; init; }

    /// <summary>Throws when a limit is set to zero or less: such a limit would refuse every document.</summary>
    public void Validate()
    {
        Positive(MaxDepth, nameof(MaxDepth));
        Positive(MaxAttributesPerElement, nameof(MaxAttributesPerElement));
        Positive(MaxNamespaceDeclarationsPerElement, nameof(MaxNamespaceDeclarationsPerElement));
        Positive(MaxNamespaceDeclarations, nameof(MaxNamespaceDeclarations));
        Positive(MaxElements, nameof(MaxElements));
        Positive(MaxCharacters, nameof(MaxCharacters));
    }

    private static void Positive(long? value, string name)
    {
        if (value is <= 0)
            throw new ArgumentOutOfRangeException(name, value, $"XML limit {name} must be positive when set.");
    }
}
