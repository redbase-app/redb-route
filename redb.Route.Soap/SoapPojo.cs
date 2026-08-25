using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Serialization;

namespace redb.Route.Soap;

/// <summary>
/// Pojo dataFormat: typed request/response bodies via <see cref="XmlSerializer"/> — the .NET analogue of
/// <c>camel-cxf</c>'s POJO mode over JAXB document/literal. The body element the serializer emits becomes the
/// <c>&lt;soap:Body&gt;</c> payload; the response payload deserializes into the configured CLR type. DTOs are
/// ordinary XML-serializable types and may be generated from a WSDL with <c>dotnet-svcutil</c>. No WCF
/// runtime and no vulnerable CoreWCF dependency.
/// </summary>
internal static class SoapPojo
{
    private static readonly ConcurrentDictionary<Type, XmlSerializer> Cache = new();

    private static XmlSerializer For(Type t) => Cache.GetOrAdd(t, static x => new XmlSerializer(x));

    /// <summary>Serializes a CLR object to an XML fragment (no declaration) for embedding in the SOAP body.</summary>
    public static string Serialize(object body)
    {
        var ser = For(body.GetType());
        // Suppress the xsi/xsd namespace noise XmlSerializer adds by default.
        var ns = new XmlSerializerNamespaces();
        ns.Add(string.Empty, string.Empty);

        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false };
        using var sw = new StringWriter();
        using (var xw = XmlWriter.Create(sw, settings))
            ser.Serialize(xw, body, ns);
        return sw.ToString();
    }

    /// <summary>Deserializes a SOAP body XML fragment into the given CLR type.</summary>
    public static object? Deserialize(string bodyXml, Type type)
    {
        if (string.IsNullOrWhiteSpace(bodyXml)) return null;
        var ser = For(type);
        using var sr = new StringReader(bodyXml);
        return ser.Deserialize(sr);
    }
}
