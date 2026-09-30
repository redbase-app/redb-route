using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace redb.Route.As4.Security;

/// <summary>
/// Finds the certificate a <c>ds:KeyInfo</c> points at, in any of the three WS-Security X.509 token
/// references eDelivery asks for: a <c>wsse:Reference</c> to a <c>wsse:BinarySecurityToken</c> in the same
/// message, a <c>ds:X509IssuerSerial</c>, or a <c>wsse:KeyIdentifier</c> with the Subject Key Identifier.
/// <para>
/// A certificate carried in the message is material, not trust: the caller decides whether it is acceptable
/// (the partner's certificate from the agreement, or one of ours for decryption).
/// </para>
/// </summary>
internal static class SecurityTokenResolver
{
    /// <summary>
    /// The certificate <paramref name="keyInfo"/> references. A token in the message is returned as is; an
    /// issuer-serial or key-identifier reference is matched against <paramref name="candidates"/>. Null when
    /// nothing matches.
    /// </summary>
    public static X509Certificate2? Resolve(XmlElement keyInfo, XmlDocument envelope, IEnumerable<X509Certificate2> candidates)
    {
        ArgumentNullException.ThrowIfNull(keyInfo);

        var str = FirstChild(keyInfo, "SecurityTokenReference", WsSecurityNames.Wsse)
            ?? throw new CryptographicException("ds:KeyInfo holds no wsse:SecurityTokenReference.");

        if (FirstChild(str, "Reference", WsSecurityNames.Wsse) is { } reference)
        {
            var uri = reference.GetAttribute("URI");
            if (!uri.StartsWith('#'))
                throw new CryptographicException($"wsse:Reference '{uri}' does not point into the message.");
            var token = FindById(envelope, uri[1..], "BinarySecurityToken", WsSecurityNames.Wsse)
                ?? throw new CryptographicException($"No wsse:BinarySecurityToken carries the id '{uri[1..]}'.");
            return FromBinarySecurityToken(token);
        }

        if (FirstChild(str, "X509Data", WsSecurityNames.Ds) is { } x509Data
            && FirstChild(x509Data, "X509IssuerSerial", WsSecurityNames.Ds) is { } issuerSerial)
        {
            var issuer = FirstChild(issuerSerial, "X509IssuerName", WsSecurityNames.Ds)?.InnerText.Trim();
            var serial = FirstChild(issuerSerial, "X509SerialNumber", WsSecurityNames.Ds)?.InnerText.Trim();
            if (issuer is null || serial is null)
                throw new CryptographicException("ds:X509IssuerSerial lacks the issuer name or the serial number.");
            // The serial is decimal in XML Signature; the issuer is compared as a parsed name, because the
            // same name can be encoded with different ASN.1 string types.
            var serialNumber = System.Numerics.BigInteger.Parse(serial, System.Globalization.CultureInfo.InvariantCulture);
            var issuerName = new X500DistinguishedName(issuer).Name;
            return candidates.FirstOrDefault(c =>
                string.Equals(c.IssuerName.Name, issuerName, StringComparison.OrdinalIgnoreCase)
                && new System.Numerics.BigInteger(c.GetSerialNumber(), isUnsigned: true, isBigEndian: false) == serialNumber);
        }

        if (FirstChild(str, "KeyIdentifier", WsSecurityNames.Wsse) is { } keyIdentifier)
        {
            if (keyIdentifier.GetAttribute("ValueType") != WsSecurityNames.X509SubjectKeyIdentifier)
                throw new CryptographicException($"wsse:KeyIdentifier of type '{keyIdentifier.GetAttribute("ValueType")}' is not supported.");
            var ski = Convert.FromBase64String(keyIdentifier.InnerText.Trim());
            return candidates.FirstOrDefault(c =>
                c.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault() is { } ext
                && Convert.FromHexString(ext.SubjectKeyIdentifier!).AsSpan().SequenceEqual(ski));
        }

        throw new CryptographicException("wsse:SecurityTokenReference uses no supported reference form.");
    }

    /// <summary>The certificate in a <c>wsse:BinarySecurityToken</c> of X.509 v3 type.</summary>
    public static X509Certificate2 FromBinarySecurityToken(XmlElement token)
    {
        var valueType = token.GetAttribute("ValueType");
        if (valueType != WsSecurityNames.X509V3)
            throw new CryptographicException($"wsse:BinarySecurityToken of type '{valueType}' is not an X.509 v3 certificate.");
        var der = Convert.FromBase64String(token.InnerText.Trim());
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificate(der);
#else
        return new X509Certificate2(der);
#endif
    }

    /// <summary>The element named <paramref name="localName"/> in <paramref name="ns"/> with <c>wsu:Id</c> (or <c>Id</c>) <paramref name="id"/>.</summary>
    public static XmlElement? FindById(XmlDocument document, string id, string localName, string ns)
    {
        XmlElement? found = null;
        foreach (XmlElement e in document.GetElementsByTagName(localName, ns))
        {
            if (e.GetAttribute("Id", WsSecurityNames.Wsu) != id && e.GetAttribute("Id") != id) continue;
            if (found is not null)
                throw new CryptographicException($"Two {localName} elements carry the id '{id}'.");
            found = e;
        }
        return found;
    }

    /// <summary>
    /// Builds a <c>ds:KeyInfo</c> that references <paramref name="certificate"/> in the requested form. For
    /// <see cref="As4KeyReference.BinarySecurityToken"/> the token itself is appended to
    /// <paramref name="security"/> — before <paramref name="insertTokenBefore"/> when given — unless an identical
    /// one is there already, and referenced by id: a receiver meets the token before the reference to it.
    /// </summary>
    public static XmlElement CreateKeyInfo(XmlDocument doc, XmlElement security, X509Certificate2 certificate, As4KeyReference form,
        XmlNode? insertTokenBefore = null)
    {
        var keyInfo = doc.CreateElement("ds", "KeyInfo", WsSecurityNames.Ds);
        var str = doc.CreateElement("wsse", "SecurityTokenReference", WsSecurityNames.Wsse);
        keyInfo.AppendChild(str);

        switch (form)
        {
            case As4KeyReference.BinarySecurityToken:
            {
                var tokenId = EnsureBinarySecurityToken(doc, security, certificate, insertTokenBefore);
                var reference = doc.CreateElement("wsse", "Reference", WsSecurityNames.Wsse);
                reference.SetAttribute("URI", "#" + tokenId);
                reference.SetAttribute("ValueType", WsSecurityNames.X509V3);
                str.AppendChild(reference);
                break;
            }
            case As4KeyReference.IssuerSerial:
            {
                var x509Data = doc.CreateElement("ds", "X509Data", WsSecurityNames.Ds);
                var issuerSerial = doc.CreateElement("ds", "X509IssuerSerial", WsSecurityNames.Ds);
                var issuer = doc.CreateElement("ds", "X509IssuerName", WsSecurityNames.Ds);
                issuer.InnerText = certificate.IssuerName.Name;
                var serial = doc.CreateElement("ds", "X509SerialNumber", WsSecurityNames.Ds);
                serial.InnerText = new System.Numerics.BigInteger(certificate.GetSerialNumber(), isUnsigned: true, isBigEndian: false)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                issuerSerial.AppendChild(issuer);
                issuerSerial.AppendChild(serial);
                x509Data.AppendChild(issuerSerial);
                str.AppendChild(x509Data);
                break;
            }
            case As4KeyReference.KeyIdentifier:
            {
                var ski = certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier
                    ?? throw new CryptographicException("The certificate has no Subject Key Identifier to reference.");
                var identifier = doc.CreateElement("wsse", "KeyIdentifier", WsSecurityNames.Wsse);
                identifier.SetAttribute("EncodingType", WsSecurityNames.Base64Binary);
                identifier.SetAttribute("ValueType", WsSecurityNames.X509SubjectKeyIdentifier);
                identifier.InnerText = Convert.ToBase64String(Convert.FromHexString(ski));
                str.AppendChild(identifier);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown key reference form.");
        }

        return keyInfo;
    }

    private static string EnsureBinarySecurityToken(XmlDocument doc, XmlElement security, X509Certificate2 certificate, XmlNode? insertBefore)
    {
        var der = Convert.ToBase64String(certificate.RawData);
        foreach (var existing in security.ChildNodes.OfType<XmlElement>()
                     .Where(e => e.LocalName == "BinarySecurityToken" && e.NamespaceURI == WsSecurityNames.Wsse))
        {
            if (existing.InnerText.Trim() == der)
                return existing.GetAttribute("Id", WsSecurityNames.Wsu);
        }

        var token = doc.CreateElement("wsse", "BinarySecurityToken", WsSecurityNames.Wsse);
        token.SetAttribute("EncodingType", WsSecurityNames.Base64Binary);
        token.SetAttribute("ValueType", WsSecurityNames.X509V3);
        var id = doc.CreateAttribute("wsu", "Id", WsSecurityNames.Wsu);
        id.Value = "X509-" + Guid.NewGuid().ToString("N");
        token.Attributes.Append(id);
        token.InnerText = der;
        if (insertBefore is null) security.AppendChild(token); else security.InsertBefore(token, insertBefore);
        return id.Value;
    }

    /// <summary>The first child element with the given name.</summary>
    public static XmlElement? FirstChild(XmlElement parent, string localName, string ns) =>
        parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == localName && e.NamespaceURI == ns);
}
