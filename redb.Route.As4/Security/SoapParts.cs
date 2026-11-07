using System.Security.Cryptography;
using System.Xml;

namespace redb.Route.As4.Security;

/// <summary>
/// The parts of a SOAP 1.2 envelope that AS4 security works on, found by namespace and position — never by
/// prefix, which senders choose freely. <c>eb:Messaging</c> counts only as a direct child of the header: a
/// copy anywhere else is not the header the message is processed by.
/// </summary>
internal sealed record SoapParts(XmlElement Header, XmlElement Body, XmlElement? Messaging, XmlElement? Security, XmlElement? Timestamp)
{
    /// <summary>Finds the parts; throws when the envelope is not SOAP 1.2 or repeats a part.</summary>
    public static SoapParts Of(XmlDocument envelope)
    {
        var root = envelope.DocumentElement;
        if (root is null || root.LocalName != "Envelope" || root.NamespaceURI != WsSecurityNames.Soap12)
            throw new CryptographicException("The document is not a SOAP 1.2 envelope.");

        var header = Single(root, "Header", WsSecurityNames.Soap12) ?? throw new CryptographicException("The envelope has no SOAP header.");
        var body = Single(root, "Body", WsSecurityNames.Soap12) ?? throw new CryptographicException("The envelope has no SOAP body.");
        var messaging = Single(header, "Messaging", WsSecurityNames.Eb);
        var security = Single(header, "Security", WsSecurityNames.Wsse);
        var timestamp = security is null ? null : Single(security, "Timestamp", WsSecurityNames.Wsu);
        return new SoapParts(header, body, messaging, security, timestamp);
    }

    /// <summary>Adds an empty <c>wsse:Security</c> header with <c>mustUnderstand="true"</c> as the first header block.</summary>
    public static XmlElement AddSecurityHeader(XmlDocument envelope, XmlElement header)
    {
        var security = envelope.CreateElement("wsse", "Security", WsSecurityNames.Wsse);
        var mustUnderstand = envelope.CreateAttribute("soapenv", "mustUnderstand", WsSecurityNames.Soap12);
        mustUnderstand.Value = "true";
        security.Attributes.Append(mustUnderstand);
        header.PrependChild(security);
        return security;
    }

    private static XmlElement? Single(XmlElement parent, string localName, string ns)
    {
        XmlElement? found = null;
        foreach (var child in parent.ChildNodes.OfType<XmlElement>())
        {
            if (child.LocalName != localName || child.NamespaceURI != ns) continue;
            if (found is not null)
                throw new CryptographicException($"{parent.LocalName} carries more than one {localName}.");
            found = child;
        }
        return found;
    }
}
