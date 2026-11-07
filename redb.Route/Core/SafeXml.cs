using System.Xml;
using System.Xml.Linq;

namespace redb.Route.Core;

/// <summary>
/// Reads XML that came from somewhere else. Every loader here goes through an <see cref="XmlReader"/>
/// with <see cref="DtdProcessing.Prohibit"/> and no resolver, which is the only shape in .NET that
/// refuses a document type declaration.
/// <para>
/// The direct loaders do not. Measured on .NET 10: a 452-byte document whose DTD declares nested
/// internal entities expands to 300 000 characters through <see cref="XmlDocument.Load(Stream)"/>,
/// through <see cref="XDocument.Load(Stream)"/> and through <see cref="XDocument.Parse(string)"/>
/// alike — LINQ to XML is no safer than the old DOM here. Setting <c>XmlResolver = null</c> does not
/// help either: it closes external entities, while the expansion is driven by internal ones (the
/// "billion laughs" shape of an XXE attack). One small message can therefore cost the worker
/// gigabytes, which is a denial of service against every route sharing the process.
/// </para>
/// <para>
/// A DTD is refused rather than expanded with a budget: SOAP forbids a document type declaration in
/// an envelope outright, and a payload that needs one is rare enough to be an explicit decision
/// rather than a default.
/// </para>
/// <para>
/// Every loader also checks the structure of the document while it reads (<see cref="XmlLimits"/>): depth,
/// attributes and namespace declarations per element, and where the caller asks, size, element count and
/// namespace declarations in the whole document. A document over a limit is refused with
/// <see cref="XmlLimitException"/> before it is built, so whatever reads it next gets a bounded input. The loaders
/// without an <see cref="XmlLimits"/> argument apply <see cref="XmlLimits.Default"/>, with <c>maxCharacters</c> as
/// the size bound.
/// </para>
/// </summary>
public static class SafeXml
{
    /// <summary>
    /// Reader settings that refuse a DTD and never resolve anything external. They do not check the structure: a
    /// reader made from them alone is not bounded by <see cref="XmlLimits"/>, see <see cref="CreateReader(Stream, XmlLimits)"/>.
    /// </summary>
    /// <param name="maxCharacters">Upper bound on the whole document, or null for no bound.</param>
    public static XmlReaderSettings Settings(long? maxCharacters = null) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = maxCharacters ?? 0,
        CloseInput = false,
    };

    /// <summary>A reader over <paramref name="stream"/> that refuses a DTD and checks <paramref name="limits"/>.</summary>
    public static XmlReader CreateReader(Stream stream, XmlLimits limits) => CreateReader(stream, limits, baseUri: null);

    /// <summary>A reader over <paramref name="text"/> that refuses a DTD and checks <paramref name="limits"/>.</summary>
    public static XmlReader CreateReader(TextReader text, XmlLimits limits)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        return new LimitedXmlReader(XmlReader.Create(text, Settings(limits.MaxCharacters)), limits);
    }

    private static XmlReader CreateReader(Stream stream, XmlLimits limits, string? baseUri)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        var inner = baseUri is null
            ? XmlReader.Create(stream, Settings(limits.MaxCharacters))
            : XmlReader.Create(stream, Settings(limits.MaxCharacters), baseUri);
        return new LimitedXmlReader(inner, limits);
    }

    /// <summary>
    /// Reads <paramref name="xml"/> to the end without building anything, and throws <see cref="XmlLimitException"/>
    /// or <see cref="XmlException"/> when it is over <paramref name="limits"/> or not well-formed. For a receiver that
    /// parses the same bytes several times: one check up front bounds every later parse.
    /// </summary>
    public static void Check(byte[] xml, XmlLimits limits)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var stream = new MemoryStream(xml, writable: false);
        using var reader = CreateReader(stream, limits);
        while (reader.Read()) { }
    }

    /// <summary>Loads a document from bytes, within <see cref="XmlLimits.Default"/> and <paramref name="maxCharacters"/>.</summary>
    public static XDocument Load(byte[] xml, LoadOptions options = LoadOptions.None, long? maxCharacters = null) =>
        Load(xml, Bounded(maxCharacters), options);

    /// <summary>Loads a document from bytes within <paramref name="limits"/>.</summary>
    public static XDocument Load(byte[] xml, XmlLimits limits, LoadOptions options = LoadOptions.None)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var stream = new MemoryStream(xml, writable: false);
        return Load(stream, limits, options);
    }

    /// <summary>Loads a document from a stream, within <see cref="XmlLimits.Default"/> and <paramref name="maxCharacters"/>.</summary>
    public static XDocument Load(Stream stream, LoadOptions options = LoadOptions.None, long? maxCharacters = null) =>
        Load(stream, Bounded(maxCharacters), options);

    /// <summary>Loads a document from a stream within <paramref name="limits"/>.</summary>
    public static XDocument Load(Stream stream, XmlLimits limits, LoadOptions options = LoadOptions.None)
    {
        using var reader = CreateReader(stream, limits);
        return XDocument.Load(reader, options);
    }

    /// <summary>Loads a document from a file path, within <see cref="XmlLimits.Default"/> and <paramref name="maxCharacters"/>.</summary>
    public static XDocument LoadFile(string path, LoadOptions options = LoadOptions.None, long? maxCharacters = null) =>
        LoadFile(path, Bounded(maxCharacters), options);

    /// <summary>Loads a document from a file path within <paramref name="limits"/>.</summary>
    public static XDocument LoadFile(string path, XmlLimits limits, LoadOptions options = LoadOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = System.IO.File.OpenRead(path);
        // The reader needs the base URI to report a usable location for an error in this file.
        using var reader = CreateReader(stream, limits, path);
        return XDocument.Load(reader, options);
    }

    /// <summary>Parses a document from text, within <see cref="XmlLimits.Default"/> and <paramref name="maxCharacters"/>.</summary>
    public static XDocument Parse(string xml, LoadOptions options = LoadOptions.None, long? maxCharacters = null) =>
        Parse(xml, Bounded(maxCharacters), options);

    /// <summary>Parses a document from text within <paramref name="limits"/>.</summary>
    public static XDocument Parse(string xml, XmlLimits limits, LoadOptions options = LoadOptions.None)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = CreateReader(new StringReader(xml), limits);
        return XDocument.Load(reader, options);
    }

    /// <summary>Parses a single element from text, within <see cref="XmlLimits.Default"/> and <paramref name="maxCharacters"/>.</summary>
    public static XElement ParseElement(string xml, LoadOptions options = LoadOptions.None, long? maxCharacters = null) =>
        ParseElement(xml, Bounded(maxCharacters), options);

    /// <summary>Parses a single element from text within <paramref name="limits"/>.</summary>
    public static XElement ParseElement(string xml, XmlLimits limits, LoadOptions options = LoadOptions.None)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = CreateReader(new StringReader(xml), limits);
        reader.MoveToContent();
        return XElement.Load(reader, options);
    }

    /// <summary>
    /// Loads an <see cref="XmlDocument"/>, the shape <c>System.Security.Cryptography.Xml</c> needs for signatures and
    /// encryption, within <see cref="XmlLimits.Default"/> and <paramref name="maxCharacters"/>.
    /// <paramref name="preserveWhitespace"/> defaults to true because canonicalization of a signed document depends on
    /// it: dropping whitespace on load makes a valid signature fail to verify.
    /// </summary>
    public static XmlDocument LoadDocument(byte[] xml, bool preserveWhitespace = true, long? maxCharacters = null) =>
        LoadDocument(xml, Bounded(maxCharacters), preserveWhitespace);

    /// <summary>Loads an <see cref="XmlDocument"/> from bytes within <paramref name="limits"/>; whitespace as in the other overload.</summary>
    public static XmlDocument LoadDocument(byte[] xml, XmlLimits limits, bool preserveWhitespace = true)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var stream = new MemoryStream(xml, writable: false);
        return LoadDocument(stream, limits, preserveWhitespace);
    }

    /// <summary>Loads an <see cref="XmlDocument"/> from a stream within <paramref name="limits"/>; whitespace as in the other overload.</summary>
    public static XmlDocument LoadDocument(Stream stream, XmlLimits limits, bool preserveWhitespace = true)
    {
        var document = new XmlDocument { PreserveWhitespace = preserveWhitespace, XmlResolver = null };
        using var reader = CreateReader(stream, limits);
        document.Load(reader);
        return document;
    }

    private static XmlLimits Bounded(long? maxCharacters) =>
        maxCharacters is null ? XmlLimits.Default : XmlLimits.Default with { MaxCharacters = maxCharacters };
}
