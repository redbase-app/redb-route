using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace redb.Route.Soap;

/// <summary>
/// WS-Security XML-Signature over the SOAP Body (Ф4b), using in-box <see cref="SignedXml"/>. The signature
/// lands inside the <c>&lt;wsse:Security&gt;</c> header with an embedded X.509 certificate (KeyInfo). This is
/// loopback-grade message signing; full WSS policy interop (STR references, exclusive-c14n inclusive prefixes,
/// encryption) is validated against a real stack at Ф8.
/// </summary>
internal static class SoapSignature
{
    public const string WsuNs = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    /// <summary>SignedXml that also resolves references to elements carrying a <c>wsu:Id</c>.</summary>
    private sealed class WssSignedXml : SignedXml
    {
        public WssSignedXml(XmlDocument document) : base(document) { }

        public override XmlElement? GetIdElement(XmlDocument document, string idValue)
        {
            var byId = base.GetIdElement(document, idValue);
            if (byId is not null) return byId;
            // idValue can come from an inbound signature's Reference URI — never interpolate quotes into XPath.
            if (idValue.IndexOfAny(new[] { '\'', '"' }) >= 0) return null;
            var nsm = new XmlNamespaceManager(document.NameTable);
            nsm.AddNamespace("wsu", WsuNs);
            return document.SelectSingleNode($"//*[@wsu:Id='{idValue}']", nsm) as XmlElement;
        }
    }

    /// <summary>Signs the SOAP Body and places the signature in the <c>&lt;wsse:Security&gt;</c> header.</summary>
    public static void SignBody(XmlDocument envelope, X509Certificate2 cert, SoapVersion version)
    {
        var soapNs = SoapEnvelope.Ns(version).NamespaceName;
        var body = (XmlElement)(envelope.GetElementsByTagName("Body", soapNs)[0]
            ?? throw new InvalidOperationException("No <soap:Body> to sign."));

        const string bodyId = "id-Body-1";
        var wsuId = envelope.CreateAttribute("wsu", "Id", WsuNs);
        wsuId.Value = bodyId;
        body.SetAttributeNode(wsuId);

        using var key = cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Signing certificate has no RSA private key.");

        var signedXml = new WssSignedXml(envelope) { SigningKey = key };
        signedXml.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;

        var reference = new Reference("#" + bodyId);
        reference.AddTransform(new XmlDsigExcC14NTransform());
        reference.DigestMethod = SignedXml.XmlDsigSHA256Url;
        signedXml.AddReference(reference);
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(cert));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        var signatureElement = signedXml.GetXml();

        var security = EnsureSecurityHeader(envelope, version);
        security.AppendChild(envelope.ImportNode(signatureElement, true));
    }

    /// <summary>Whether the envelope carries a <c>ds:Signature</c>.</summary>
    public static bool HasSignature(XmlDocument envelope) =>
        envelope.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl).Count > 0;

    /// <summary>
    /// Verifies the Body signature. When <paramref name="expectedCert"/> is supplied (the partner's known
    /// certificate), the signer MUST be that certificate — this authenticates the sender. When it is null,
    /// only cryptographic integrity against the embedded key is checked (this does NOT authenticate the
    /// sender — any self-signed cert would pass). In both cases the signed reference MUST cover the
    /// <c>&lt;soap:Body&gt;</c>, defeating signature-wrapping where a decoy element is signed and the Body left
    /// unsigned.
    /// </summary>
    public static bool VerifyBody(XmlDocument envelope, X509Certificate2? expectedCert, SoapVersion version)
    {
        if (envelope.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0] is not XmlElement sig)
            return false;

        var signedXml = new WssSignedXml(envelope);
        signedXml.LoadXml(sig);

        bool cryptoOk;
        if (expectedCert is not null)
        {
            // Authenticated: the signing cert must be the expected partner cert, then verify with its key.
            var signer = SigningCertFrom(signedXml);
            if (signer is null || !string.Equals(signer.Thumbprint, expectedCert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                return false;
            cryptoOk = signedXml.CheckSignature(expectedCert, verifySignatureOnly: true);
        }
        else
        {
            // Integrity only, against the embedded key — does not establish who the sender is.
            cryptoOk = signedXml.CheckSignature();
        }

        return cryptoOk && SignedReferenceCoversBody(signedXml, envelope, version);
    }

    /// <summary>The X.509 certificate embedded in the signature's KeyInfo, if any.</summary>
    private static X509Certificate2? SigningCertFrom(SignedXml signedXml)
    {
        if (signedXml.KeyInfo is null) return null;
        foreach (var clause in signedXml.KeyInfo)
            if (clause is KeyInfoX509Data x509 && x509.Certificates.Count > 0)
                return x509.Certificates[0] as X509Certificate2;
        return null;
    }

    /// <summary>True when a signed reference resolves to the <c>&lt;soap:Body&gt;</c> the processing path uses.</summary>
    private static bool SignedReferenceCoversBody(SignedXml signedXml, XmlDocument envelope, SoapVersion version)
    {
        var soapNs = SoapEnvelope.Ns(version).NamespaceName;

        // CRITICAL: resolve the Body the SAME way SoapEnvelope.Parse does — the Envelope's DIRECT-CHILD Body —
        // not GetElementsByTagName (document order, any depth). Otherwise an attacker can nest a genuinely
        // signed Body inside <Header> (first in document order) so the anti-wrapping check passes, while Parse
        // hands the route a different, unsigned direct-child Body: an XML signature-wrapping bypass. Also
        // require exactly one direct-child Body, so a second/ambiguous Body cannot be smuggled in.
        if (envelope.DocumentElement is not { } env
            || env.LocalName != "Envelope" || env.NamespaceURI != soapNs) return false;

        XmlElement? body = null;
        var bodyCount = 0;
        foreach (var node in env.ChildNodes)
            if (node is XmlElement el && el.LocalName == "Body" && el.NamespaceURI == soapNs)
            {
                body ??= el;
                bodyCount++;
            }
        if (body is null || bodyCount != 1) return false;

        foreach (Reference r in signedXml.SignedInfo.References)
        {
            var uri = r.Uri;
            if (string.IsNullOrEmpty(uri) || uri[0] != '#') continue;
            if (ReferenceEquals(signedXml.GetIdElement(envelope, uri[1..]), body)) return true;
        }
        return false;
    }

    /// <summary>Returns the <c>&lt;wsse:Security&gt;</c> header element, creating the header/element if absent.</summary>
    internal static XmlElement EnsureSecurityHeader(XmlDocument envelope, SoapVersion version)
    {
        var soap = SoapEnvelope.Ns(version).NamespaceName;
        var existing = envelope.GetElementsByTagName("Security", SoapSecurity.WsseNs);
        if (existing.Count > 0) return (XmlElement)existing[0]!;

        var envelopeEl = (XmlElement)envelope.GetElementsByTagName("Envelope", soap)[0]!;
        var header = envelope.GetElementsByTagName("Header", soap).Count > 0
            ? (XmlElement)envelope.GetElementsByTagName("Header", soap)[0]!
            : (XmlElement)envelopeEl.InsertBefore(envelope.CreateElement("soap", "Header", soap), envelopeEl.FirstChild)!;

        var security = envelope.CreateElement("wsse", "Security", SoapSecurity.WsseNs);
        header.AppendChild(security);
        return security;
    }
}
