using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace redb.Route.Soap;

/// <summary>
/// WS-Security XML-Encryption of the SOAP Body payload (in-box <see cref="EncryptedXml"/>), in the layout real
/// WSS stacks (WCF, Apache CXF/WSS4J) produce and expect: the payload is encrypted with a per-message AES-256
/// key, the <c>&lt;xenc:EncryptedKey&gt;</c> (that key RSA-wrapped to the recipient) lives in the
/// <c>&lt;wsse:Security&gt;</c> header and points at the <c>&lt;xenc:EncryptedData&gt;</c> in the Body through a
/// <c>&lt;xenc:ReferenceList&gt;</c>/<c>&lt;xenc:DataReference&gt;</c>. Decryption also accepts the older inline
/// layout (EncryptedKey nested in EncryptedData) for backward compatibility. Runs on the patched
/// <c>System.Security.Cryptography.Xml</c> 9.0.18 (CVE-2026-50648 fixed).
/// </summary>
internal static class SoapEncryption
{
    public const string XmlEncNs = "http://www.w3.org/2001/04/xmlenc#";

    /// <summary>Encrypts the Body's payload element to the recipient's certificate (standard WSS layout).</summary>
    public static void EncryptBody(XmlDocument doc, X509Certificate2 recipientCert, SoapVersion version)
    {
        var soapNs = SoapEnvelope.Ns(version).NamespaceName;
        var body = (XmlElement)(doc.GetElementsByTagName("Body", soapNs)[0]
            ?? throw new InvalidOperationException("No <soap:Body> to encrypt."));
        // ALL child elements, not just the first: a document/literal Body can carry several, and encrypting
        // only the first would leak the rest in cleartext. They share one session key (standard WSS).
        var payloads = body.ChildNodes.OfType<XmlElement>().ToList();
        if (payloads.Count == 0)
            throw new InvalidOperationException("Empty <soap:Body> — nothing to encrypt.");

        // 1. Fresh AES-256 session key, RSA-wrapped to the recipient (OAEP). The EncryptedKey's ReferenceList
        //    points at every EncryptedData below.
        using var sessionKey = Aes.Create();
        sessionKey.KeySize = 256;
        using var rsa = recipientCert.GetRSAPublicKey()
            ?? throw new InvalidOperationException("Recipient certificate has no RSA public key.");
        var wrappedKey = EncryptedXml.EncryptKey(sessionKey.Key, rsa, useOAEP: true);

        var encryptedKey = new EncryptedKey
        {
            EncryptionMethod = new EncryptionMethod(EncryptedXml.XmlEncRSAOAEPUrl),
            CipherData = new CipherData(wrappedKey),
        };
        encryptedKey.KeyInfo.AddClause(new KeyInfoX509Data(recipientCert)); // recipient hint

        // 2. Encrypt each Body child in place; reference each EncryptedData from the EncryptedKey.
        var encXml = new EncryptedXml();
        foreach (var payload in payloads)
        {
            var cipher = encXml.EncryptData(payload, sessionKey, content: false);
            var edId = "ED-" + Guid.NewGuid().ToString("N");
            var encryptedData = new EncryptedData
            {
                Type = EncryptedXml.XmlEncElementUrl,       // whole element replaced
                Id = edId,
                EncryptionMethod = new EncryptionMethod(EncryptedXml.XmlEncAES256Url),
                CipherData = new CipherData(cipher),
            };
            encryptedKey.AddReference(new DataReference("#" + edId));
            EncryptedXml.ReplaceElement(payload, encryptedData, content: false);
        }

        // 3. Put the EncryptedKey in the Security header.
        var security = SoapSignature.EnsureSecurityHeader(doc, version);
        security.AppendChild(doc.ImportNode(encryptedKey.GetXml(), true));
    }

    /// <summary>Whether the document carries an <c>EncryptedData</c> element.</summary>
    public static bool HasEncryptedData(XmlDocument doc) =>
        doc.GetElementsByTagName("EncryptedData", XmlEncNs).Count > 0;

    /// <summary>
    /// Decrypts the Body payload in place using our private key. Accepts both the standard WSS layout
    /// (EncryptedKey in the Security header with a ReferenceList) and the legacy inline layout.
    /// </summary>
    public static void DecryptBody(XmlDocument doc, X509Certificate2 ourCertWithKey)
    {
        var encKeyEl = (XmlElement)(doc.GetElementsByTagName("EncryptedKey", XmlEncNs)[0]
            ?? throw new InvalidOperationException("No EncryptedKey to decrypt."));
        var encKey = new EncryptedKey();
        encKey.LoadXml(encKeyEl);

        using var rsa = ourCertWithKey.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Decryption certificate has no RSA private key.");
        var useOaep = encKey.EncryptionMethod?.KeyAlgorithm == EncryptedXml.XmlEncRSAOAEPUrl;
        var sessionKey = EncryptedXml.DecryptKey(encKey.CipherData.CipherValue, rsa, useOaep);

        // Resolve the EncryptedData(s) the key references; fall back to every EncryptedData in the document.
        var targets = new List<XmlElement>();
        foreach (EncryptedReference reference in encKey.ReferenceList)
            if (reference.Uri is { Length: > 1 } uri && uri[0] == '#' && FindById(doc, uri[1..]) is { } el)
                targets.Add(el);
        if (targets.Count == 0)
            targets.AddRange(doc.GetElementsByTagName("EncryptedData", XmlEncNs).OfType<XmlElement>());

        foreach (var edEl in targets)
        {
            var encData = new EncryptedData();
            encData.LoadXml(edEl);
            using var alg = CreateSymmetric(encData.EncryptionMethod?.KeyAlgorithm);
            alg.Key = sessionKey;
            var eXml = new EncryptedXml(doc);
            var plain = eXml.DecryptData(encData, alg);
            eXml.ReplaceData(edEl, plain);
        }
    }

    private static XmlElement? FindById(XmlDocument doc, string id) =>
        doc.GetElementsByTagName("EncryptedData", XmlEncNs)
            .OfType<XmlElement>()
            .FirstOrDefault(e => e.GetAttribute("Id") == id);

    private static SymmetricAlgorithm CreateSymmetric(string? algorithm)
    {
        var aes = Aes.Create();
        aes.KeySize = algorithm switch
        {
            EncryptedXml.XmlEncAES128Url => 128,
            EncryptedXml.XmlEncAES192Url => 192,
            _ => 256, // AES-256 default (also our own EncryptBody)
        };
        return aes;
    }
}
