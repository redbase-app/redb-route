using System.Text;
using System.Xml.Linq;

namespace redb.Route.Soap;

/// <summary>Result of parsing a SOAP envelope: the body payload, or a fault.</summary>
internal readonly record struct SoapParseResult(bool IsFault, string? FaultCode, string? FaultString, string BodyXml);

/// <summary>
/// Builds and parses SOAP 1.1 / 1.2 envelopes with <see cref="System.Xml.Linq"/> (in-box, no WCF).
/// Payload mode: the exchange body is the content of <c>&lt;soap:Body&gt;</c>. Faults are detected for
/// both versions (1.1 <c>faultcode</c>/<c>faultstring</c>, 1.2 <c>Code</c>/<c>Reason</c>).
/// </summary>
internal static class SoapEnvelope
{
    public const string Ns11 = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string Ns12 = "http://www.w3.org/2003/05/soap-envelope";

    public static XNamespace Ns(SoapVersion v) => v == SoapVersion.Soap12 ? Ns12 : Ns11;

    /// <summary>The HTTP Content-Type for the version (SOAPAction is folded into it for 1.2).</summary>
    public static string ContentType(SoapVersion v, string? action) =>
        v == SoapVersion.Soap12
            ? $"application/soap+xml; charset=utf-8{(string.IsNullOrEmpty(action) ? "" : $"; action=\"{action}\"")}"
            : "text/xml; charset=utf-8";

    /// <summary>
    /// Wraps <paramref name="bodyXml"/> (an XML fragment) in an envelope. Optional <paramref name="headerXml"/>
    /// becomes the <c>&lt;soap:Header&gt;</c> content.
    /// </summary>
    public static byte[] Build(string bodyXml, SoapVersion version, string? headerXml = null)
    {
        var soap = Ns(version);
        var body = new XElement(soap + "Body");
        if (!string.IsNullOrWhiteSpace(bodyXml))
            body.Add(ParseBodyContent(bodyXml));

        var envelope = new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName));
        if (!string.IsNullOrWhiteSpace(headerXml))
            envelope.Add(new XElement(soap + "Header", ParseFragments(headerXml!)));
        envelope.Add(body);

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), envelope);
        using var ms = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false }))
            doc.Save(writer);
        return ms.ToArray();
    }

    /// <summary>Parses a response/request envelope into its body payload or a fault.</summary>
    public static SoapParseResult Parse(byte[] envelope, SoapVersion version)
    {
        var doc = XDocument.Load(new MemoryStream(envelope));
        var soap = Ns(version);
        var body = doc.Root?.Element(soap + "Body")
            ?? throw new FormatException("SOAP envelope has no <Body>.");

        var fault = body.Element(soap + "Fault");
        if (fault is not null)
        {
            if (version == SoapVersion.Soap12)
            {
                var code = fault.Element(soap + "Code")?.Element(soap + "Value")?.Value;
                var reason = fault.Element(soap + "Reason")?.Element(soap + "Text")?.Value;
                return new SoapParseResult(true, code, reason, fault.ToString());
            }
            // 1.1: faultcode / faultstring live in the empty namespace.
            var faultcode = fault.Element("faultcode")?.Value;
            var faultstring = fault.Element("faultstring")?.Value;
            return new SoapParseResult(true, faultcode, faultstring, fault.ToString());
        }

        var payload = body.Elements().FirstOrDefault();
        return new SoapParseResult(false, null, null, payload?.ToString() ?? string.Empty);
    }

    /// <summary>Extracts the <c>&lt;soap:Header&gt;</c> child elements (the envelope header plane), or empty.</summary>
    public static IEnumerable<XElement> ReadHeaders(byte[] envelope, SoapVersion version)
    {
        var doc = XDocument.Load(new MemoryStream(envelope));
        var soap = Ns(version);
        var header = doc.Root?.Element(soap + "Header");
        return header?.Elements() ?? Enumerable.Empty<XElement>();
    }

    /// <summary>
    /// Namespaces for the prefixes a fault code is most likely to use, so a route naming a standard
    /// fault does not have to repeat the URI every time. A prefix that is not here, and whose namespace
    /// the route did not give, is written as it was handed over — the alternative would be inventing a
    /// namespace for it, and a wrong namespace is worse than an unresolved prefix.
    /// </summary>
    private static readonly Dictionary<string, string> WellKnownFaultPrefixes = new(StringComparer.Ordinal)
    {
        ["wst"] = "http://docs.oasis-open.org/ws-sx/ws-trust/200512",
        ["wsse"] = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd",
        ["wsu"] = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd",
        ["wsa"] = "http://www.w3.org/2005/08/addressing",
        ["wsrm"] = "http://docs.oasis-open.org/ws-rx/wsrm/200702",
    };

    /// <summary>
    /// The fault code for "the caller sent something we cannot process", spelled for this SOAP version:
    /// <c>soap:Sender</c> in 1.2, <c>soap:Client</c> in 1.1.
    /// <para>
    /// This is not cosmetic. SOAP 1.2 defines the other value, <c>Receiver</c> (the 1.1 <c>Server</c>),
    /// as a failure "attributable to the processing of the message rather than to the contents of the
    /// message itself" — in practice a retry hint. A request we could not even parse is the opposite:
    /// the same bytes will fail again, so a caller that branches on the code (and a retry handler is
    /// exactly such a caller) must be told the fault is its own, or it will re-send until it gives up.
    /// </para>
    /// </summary>
    public static string SenderCode(SoapVersion version) =>
        version == SoapVersion.Soap12 ? "soap:Sender" : "soap:Client";

    /// <summary>
    /// Builds a SOAP Fault envelope (for a consumer returning an error).
    /// <para>
    /// A fault code is a <b>QName</b> in both SOAP versions, so a prefixed code such as
    /// <c>wst:FailedAuthentication</c> is only meaningful when the prefix is bound on the envelope. It is
    /// declared here for that reason: a code whose prefix nothing declares is not a code a strict client
    /// can resolve, and the clients that branch on fault codes are exactly the strict ones.
    /// </para>
    /// </summary>
    /// <param name="faultCodeNamespace">
    /// Namespace for the prefix in <paramref name="faultCode"/>. Optional: standard prefixes are known,
    /// and this is for a code in a namespace of the caller's own.
    /// </param>
    public static byte[] BuildFault(string faultString, SoapVersion version, string? faultCode = null,
        string? faultCodeNamespace = null)
    {
        var soap = Ns(version);
        var code = faultCode ?? (version == SoapVersion.Soap12 ? "soap:Receiver" : "soap:Server");

        XElement fault = version == SoapVersion.Soap12
            ? new XElement(soap + "Fault",
                new XElement(soap + "Code", new XElement(soap + "Value", code)),
                new XElement(soap + "Reason", new XElement(soap + "Text", faultString)))
            : new XElement(soap + "Fault",
                new XElement("faultcode", code),
                new XElement("faultstring", faultString));

        var envelope = new XElement(soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", soap.NamespaceName),
            new XElement(soap + "Body", fault));

        // The `soap` prefix is already bound above, so a soap:* code needs nothing further.
        if (ResolveFaultPrefix(code, faultCodeNamespace) is var (prefix, ns) && prefix is not null)
            envelope.Add(new XAttribute(XNamespace.Xmlns + prefix, ns!));

        return Encoding.UTF8.GetBytes(new XDocument(envelope).ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>
    /// Works out which prefix a fault code uses and what to bind it to, or nothing when there is no
    /// prefix, when it is already bound, or when its namespace is unknown and was not supplied.
    /// </summary>
    private static (string? Prefix, string? Namespace) ResolveFaultPrefix(string code, string? supplied)
    {
        var colon = code.IndexOf(':');
        if (colon <= 0) return (null, null);

        var prefix = code[..colon];
        if (prefix == "soap") return (null, null);

        if (!string.IsNullOrEmpty(supplied)) return (prefix, supplied);

        return WellKnownFaultPrefixes.TryGetValue(prefix, out var ns) ? (prefix, ns) : (null, null);
    }

    private static object ParseBodyContent(string xml)
    {
        // The common case is a single Body child element; XElement.Parse also skips a leading XML prolog.
        try { return XElement.Parse(xml, LoadOptions.PreserveWhitespace); }
        // A document/literal body may legitimately carry several sibling elements — wrap-parse and add all.
        catch (System.Xml.XmlException) { return ParseFragments(xml).Cast<object>().ToArray(); }
    }

    /// <summary>Parses one or more sibling XML elements (e.g. several <c>&lt;soap:Header&gt;</c> children).</summary>
    private static IEnumerable<XElement> ParseFragments(string xml)
    {
        // Wrap so multiple top-level elements parse; each element carries its own inline xmlns.
        var wrapped = XElement.Parse($"<soapFragmentWrap>{xml}</soapFragmentWrap>", LoadOptions.PreserveWhitespace);
        return wrapped.Elements();
    }
}
