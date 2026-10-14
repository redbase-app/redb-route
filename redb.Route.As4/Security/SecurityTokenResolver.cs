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

    /// <summary>
    /// The certification path a <c>ds:KeyInfo</c> references, when it points at a <c>wsse:BinarySecurityToken</c> of
    /// type <c>X509PKIPathv1</c> in the same message; null for any other reference form or token type.
    /// </summary>
    public static IReadOnlyList<X509Certificate2>? PkiPathOf(XmlElement keyInfo, XmlDocument envelope)
    {
        ArgumentNullException.ThrowIfNull(keyInfo);
        if (FirstChild(keyInfo, "SecurityTokenReference", WsSecurityNames.Wsse) is not { } str
            || FirstChild(str, "Reference", WsSecurityNames.Wsse) is not { } reference)
            return null;
        var uri = reference.GetAttribute("URI");
        if (!uri.StartsWith('#'))
            throw new CryptographicException($"wsse:Reference '{uri}' does not point into the message.");
        var token = FindById(envelope, uri[1..], "BinarySecurityToken", WsSecurityNames.Wsse)
            ?? throw new CryptographicException($"No wsse:BinarySecurityToken carries the id '{uri[1..]}'.");
        return token.GetAttribute("ValueType") == PkiPath.ValueType
            ? PkiPath.Decode(Convert.FromBase64String(token.InnerText.Trim()))
            : null;
    }

    /// <summary>
    /// The certificate in a <c>wsse:BinarySecurityToken</c>: the token itself for the X.509 v3 type, the last
    /// certificate of the path — the one the path is for — for <c>X509PKIPathv1</c>.
    /// </summary>
    public static X509Certificate2 FromBinarySecurityToken(XmlElement token)
    {
        var valueType = token.GetAttribute("ValueType");
        if (valueType == PkiPath.ValueType)
        {
            var path = PkiPath.Decode(Convert.FromBase64String(token.InnerText.Trim()));
            PkiPath.Release(path, keep: path[^1]);
            return path[^1];
        }
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
        XmlNode? insertTokenBefore = null, IReadOnlyList<X509Certificate2>? path = null)
    {
        var keyInfo = doc.CreateElement("ds", "KeyInfo", WsSecurityNames.Ds);
        var str = doc.CreateElement("wsse", "SecurityTokenReference", WsSecurityNames.Wsse);
        keyInfo.AppendChild(str);

        switch (form)
        {
            case As4KeyReference.BinarySecurityToken:
            {
                var tokenId = EnsureBinarySecurityToken(doc, security, Convert.ToBase64String(certificate.RawData), WsSecurityNames.X509V3, insertTokenBefore);
                var reference = doc.CreateElement("wsse", "Reference", WsSecurityNames.Wsse);
                reference.SetAttribute("URI", "#" + tokenId);
                reference.SetAttribute("ValueType", WsSecurityNames.X509V3);
                str.AppendChild(reference);
                break;
            }
            case As4KeyReference.PkiPath:
            {
                // The certificate with its authorities, outermost first (ITU-T X.509 PkiPath): ordered and checked once,
                // when the node is resolved (As4Node.SigningPath). A certificate that is its own authority is a path of one.
                var signingPath = path ?? [certificate];
                if (signingPath.Count == 0 || signingPath[^1].Thumbprint != certificate.Thumbprint)
                    throw new CryptographicException("The certification path does not end with the signing certificate.");
                var tokenId = EnsureBinarySecurityToken(doc, security, Convert.ToBase64String(PkiPath.Encode(signingPath)), PkiPath.ValueType, insertTokenBefore);
                var reference = doc.CreateElement("wsse", "Reference", WsSecurityNames.Wsse);
                reference.SetAttribute("URI", "#" + tokenId);
                reference.SetAttribute("ValueType", PkiPath.ValueType);
                str.AppendChild(reference);
                // WS-I Basic Security Profile 1.1 R5215: a reference to a PKIPath token names its type on the
                // wsse:SecurityTokenReference itself; WSS4J (Domibus, Holodeck) refuses the message without it.
                var tokenType = doc.CreateAttribute("wsse11", "TokenType", WsSecurityNames.Wsse11);
                tokenType.Value = PkiPath.ValueType;
                str.Attributes.Append(tokenType);
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

    private static string EnsureBinarySecurityToken(XmlDocument doc, XmlElement security, string der, string valueType, XmlNode? insertBefore)
    {
        foreach (var existing in security.ChildNodes.OfType<XmlElement>()
                     .Where(e => e.LocalName == "BinarySecurityToken" && e.NamespaceURI == WsSecurityNames.Wsse))
        {
            if (existing.InnerText.Trim() == der && existing.GetAttribute("ValueType") == valueType)
                return existing.GetAttribute("Id", WsSecurityNames.Wsu);
        }

        var token = doc.CreateElement("wsse", "BinarySecurityToken", WsSecurityNames.Wsse);
        token.SetAttribute("EncodingType", WsSecurityNames.Base64Binary);
        token.SetAttribute("ValueType", valueType);
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
