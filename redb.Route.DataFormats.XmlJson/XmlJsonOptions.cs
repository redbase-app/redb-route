namespace redb.Route.DataFormats.XmlJson;

/// <summary>How element and attribute names carry their XML namespaces in JSON.</summary>
public enum XmlJsonNamespaces
{
    /// <summary>
    /// Names keep the prefix of the document (<c>"soap:Envelope"</c>), and namespace declarations appear as attributes
    /// where the document declares them (<c>"@xmlns:soap": "uri"</c>).
    /// </summary>
    Keep,

    /// <summary>Names are local names (<c>"Envelope"</c>), and namespace declarations are left out.</summary>
    Drop,
}

/// <summary>Options of the xmljson data format.</summary>
public sealed class XmlJsonOptions
{
    /// <summary>
    /// The key of an element's text when the element also has attributes or child elements. Default <c>#text</c>, as
    /// Json.NET writes it; <c>$</c> matches WSO2 Micro Integrator.
    /// </summary>
    public string TextKey { get; set; } = "#text";

    /// <summary>The prefix of an attribute's key. Default <c>@</c>, as Json.NET and WSO2 write it.</summary>
    public string AttributePrefix { get; set; } = "@";

    /// <summary>How names carry their namespaces. Default <see cref="XmlJsonNamespaces.Keep"/>.</summary>
    public XmlJsonNamespaces Namespaces { get; set; } = XmlJsonNamespaces.Keep;

    /// <summary>
    /// Paths of the elements that are always a JSON array, even when the document holds one of them:
    /// <c>/Order/Items/Item</c> from the root, <c>//Line</c> at any depth. A step is a local name, or a
    /// <c>prefix:name</c> compared with the name as the document writes it.
    /// </summary>
    public IList<string> Arrays { get; } = new List<string>();

    /// <summary>
    /// The plural-wrapper rule, off by default: a parent named as the English plural of its children's name
    /// (<c>Incidents</c> holding only <c>Incident</c>) holds a list, so its children are an array even when there is
    /// one. Regular plurals (<c>-s</c>, <c>-es</c>, <c>-ies</c>) and a short list of irregular ones
    /// (<c>Children</c> / <c>Child</c>); silent on every other name.
    /// </summary>
    public bool Plural { get; set; }

    /// <summary>
    /// The root element of JSON to XML. Without it the JSON is an object of one key, and that key is the root; with it
    /// the whole JSON is the root's content. A name with a prefix (<c>o:Order</c>) needs the prefix declared by an
    /// <c>@xmlns:o</c> key.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>
    /// Prefixes for the namespaces of JSON to XML by a schema, prefix to namespace URI (<c>"o" = "urn:orders"</c>).
    /// A namespace the JSON declares with an <c>@xmlns:p</c> key keeps that prefix; one neither declares gets
    /// <c>ns0</c>, <c>ns1</c> and so on. Every declaration goes on the root element.
    /// </summary>
    public IDictionary<string, string> Prefixes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the output, JSON or XML, is indented. Default <c>false</c>.</summary>
    public bool Indent { get; set; }

    /// <summary>
    /// Path of the XSD that describes the documents: which elements repeat, the types of values, nillable elements.
    /// Its imports and includes are read from files under <see cref="SchemaRoot"/>, never from the network.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// The directory the imports and includes of <see cref="Schema"/> may read from; a relative one is relative to the
    /// schema's own directory (<c>..</c> for a schema in <c>maindoc/</c> that imports <c>../common/</c>). Default: the
    /// directory of the schema itself.
    /// </summary>
    public string? SchemaRoot { get; set; }

    /// <summary>A schema set built in code, in place of <see cref="Schema"/>; it is compiled when the format first uses it.</summary>
    public System.Xml.Schema.XmlSchemaSet? SchemaSet { get; set; }
}
