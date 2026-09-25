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
/// rather than a default. <paramref name="maxCharacters"/> bounds the document itself where the
/// caller knows a sane size.
/// </para>
/// </summary>
public static class SafeXml
{
    /// <summary>Reader settings that refuse a DTD and never resolve anything external.</summary>
    /// <param name="maxCharacters">Upper bound on the whole document, or null for no bound.</param>
    public static XmlReaderSettings Settings(long? maxCharacters = null) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = maxCharacters ?? 0,
        CloseInput = false,
    };

    /// <summary>Loads a document from bytes.</summary>
    public static XDocument Load(byte[] xml, LoadOptions options = LoadOptions.None, long? maxCharacters = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var stream = new MemoryStream(xml, writable: false);
        return Load(stream, options, maxCharacters);
    }

    /// <summary>Loads a document from a stream.</summary>
    public static XDocument Load(Stream stream, LoadOptions options = LoadOptions.None, long? maxCharacters = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = XmlReader.Create(stream, Settings(maxCharacters));
        return XDocument.Load(reader, options);
    }

    /// <summary>Loads a document from a file path.</summary>
    public static XDocument LoadFile(string path, LoadOptions options = LoadOptions.None, long? maxCharacters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = System.IO.File.OpenRead(path);
        // The reader needs the base URI to report a usable location for an error in this file.
        using var reader = XmlReader.Create(stream, Settings(maxCharacters), path);
        return XDocument.Load(reader, options);
    }

    /// <summary>Parses a document from text.</summary>
    public static XDocument Parse(string xml, LoadOptions options = LoadOptions.None, long? maxCharacters = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = XmlReader.Create(new StringReader(xml), Settings(maxCharacters));
        return XDocument.Load(reader, options);
    }

    /// <summary>Parses a single element from text.</summary>
    public static XElement ParseElement(string xml, LoadOptions options = LoadOptions.None, long? maxCharacters = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = XmlReader.Create(new StringReader(xml), Settings(maxCharacters));
        reader.MoveToContent();
        return XElement.Load(reader, options);
    }

    /// <summary>
    /// Loads an <see cref="XmlDocument"/> — the shape <c>System.Security.Cryptography.Xml</c> needs
    /// for signatures and encryption. <paramref name="preserveWhitespace"/> defaults to true because
    /// canonicalization of a signed document depends on it: dropping whitespace on load makes a valid
    /// signature fail to verify.
    /// </summary>
    public static XmlDocument LoadDocument(byte[] xml, bool preserveWhitespace = true, long? maxCharacters = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var document = new XmlDocument { PreserveWhitespace = preserveWhitespace, XmlResolver = null };
        using var stream = new MemoryStream(xml, writable: false);
        using var reader = XmlReader.Create(stream, Settings(maxCharacters));
        document.Load(reader);
        return document;
    }
}
