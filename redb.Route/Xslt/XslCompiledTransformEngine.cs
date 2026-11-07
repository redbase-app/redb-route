using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using System.Xml.Xsl;

namespace redb.Route.Xslt;

/// <summary>
/// Built-in <see cref="IXsltEngine"/> backed by the BCL <see cref="XslCompiledTransform"/>
/// (<c>System.Xml.Xsl</c>) — zero external dependencies, <b>XSLT 1.0</b> (like Apache Camel's default
/// JAXP engine; use a Saxon-backed engine for 2.0/3.0). The stylesheet is compiled once at construction
/// and the compiled template is reused for every transform (the compilation is the expensive part).
/// <para>
/// The compiled transform is loaded with <see cref="XsltSettings.Default"/> — inline <c>script</c> and
/// the <c>document()</c> function are disabled, which is the safe default for stylesheets coming from
/// outside the application.
/// </para>
/// </summary>
public sealed class XslCompiledTransformEngine : IXsltEngine
{
    private readonly XslCompiledTransform _compiled;

    private XslCompiledTransformEngine(XslCompiledTransform compiled) => _compiled = compiled;

    /// <summary>
    /// Compiles the engine from a stylesheet file (or URL). The path is used as the base URI, so
    /// <c>xsl:import</c> / <c>xsl:include</c> resolve relative to the stylesheet.
    /// </summary>
    public static XslCompiledTransformEngine FromFile(string stylesheetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stylesheetPath);
        var xslt = new XslCompiledTransform();
        xslt.Load(stylesheetPath);
        return new XslCompiledTransformEngine(xslt);
    }

    /// <summary>
    /// Compiles the engine from an inline stylesheet document. Self-contained stylesheets only —
    /// <c>xsl:import</c>/<c>xsl:include</c> have no base URI to resolve against.
    /// </summary>
    public static XslCompiledTransformEngine FromContent(string stylesheetXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stylesheetXml);
        var xslt = new XslCompiledTransform();
        using var reader = XmlReader.Create(new StringReader(stylesheetXml));
        xslt.Load(reader);
        return new XslCompiledTransformEngine(xslt);
    }

    /// <inheritdoc />
    public object Transform(object? body, XsltOutput output, IReadOnlyDictionary<string, object?>? parameters)
    {
        using var input = CreateInputReader(body);

        var args = new XsltArgumentList();
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                if (value is null) continue;
                // Names that aren't valid XSLT parameter identifiers (e.g. a header like "Content-Type"
                // is fine, but arbitrary keys may not be) are skipped rather than failing the transform —
                // matching Camel, which silently ignores headers that aren't declared/usable as params.
                try { args.AddParam(name, string.Empty, value); }
                catch (ArgumentException) { /* not a usable parameter name — skip */ }
            }
        }

        // Using the TextWriter / Stream overloads lets XslCompiledTransform honour the stylesheet's
        // xsl:output method (xml / html / text) and encoding automatically.
        switch (output)
        {
            case XsltOutput.Bytes:
            {
                using var ms = new MemoryStream();
                _compiled.Transform(input, args, ms);
                return ms.ToArray();
            }
            case XsltOutput.Dom:
            {
                var doc = new XmlDocument();
                using var writer = doc.CreateNavigator()!.AppendChild();
                _compiled.Transform(input, args, writer);
                writer.Close();
                return doc;
            }
            default: // String
            {
                using var sw = new StringWriter();
                _compiled.Transform(input, args, sw);
                return sw.ToString();
            }
        }
    }

    private static XmlReader CreateInputReader(object? body) => body switch
    {
        XmlReader reader => reader,
        string s => XmlReader.Create(new StringReader(s)),
        byte[] bytes => XmlReader.Create(new MemoryStream(bytes)),
        Stream stream => XmlReader.Create(stream),
        XNode node => node.CreateReader(), // XDocument / XElement (System.Xml.Linq)
        IXPathNavigable navigable => navigable.CreateNavigator()!.ReadSubtree(),
        _ => throw new NotSupportedException(
            $"XSLT input body type '{body?.GetType().Name ?? "null"}' is not supported. " +
            "Provide XML as string, byte[], Stream, XmlReader, XDocument/XElement, or IXPathNavigable."),
    };
}
