using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using redb.Route.As4.Mime;

namespace redb.Route.As4.Security;

/// <summary>
/// WS-Security processing of AS4 messages: encrypting and decrypting the payload parts, signing and verifying
/// (<see cref="As4Signature"/>). Order on receipt is decrypt, then verify, then
/// decompress — the reverse of compress, sign, encrypt on the sending side (AS4 profile §3.1).
/// </summary>
internal static class As4SecurityEngine
{
    // ── Decryption ───────────────────────────────────────────────────────────

    /// <summary>
    /// Decrypts every attachment the <c>xenc:EncryptedKey</c> elements of the security header list, in place:
    /// the part content becomes the plaintext and its type the <c>xenc:EncryptedData/@MimeType</c>. Returns
    /// the Content-IDs that were decrypted. Throws <see cref="CryptographicException"/> on anything that does
    /// not decrypt — callers answer every such failure with one code and one text (no decryption oracle).
    /// </summary>
    public static IReadOnlyList<string> Decrypt(SwaMessage message, IEnumerable<X509Certificate2> ourCertificates)
    {
        ArgumentNullException.ThrowIfNull(message);
        var ours = ourCertificates.Where(c => c.HasPrivateKey).ToList();
        var security = SecurityHeader(message.Envelope);
        var decrypted = new List<string>();

        foreach (var encryptedKey in Children(security, "EncryptedKey", WsSecurityNames.Xenc))
        {
            var keyInfo = SecurityTokenResolver.FirstChild(encryptedKey, "KeyInfo", WsSecurityNames.Ds)
                ?? throw new CryptographicException("xenc:EncryptedKey has no ds:KeyInfo.");
            var recipient = SecurityTokenResolver.Resolve(keyInfo, message.Envelope, ours)
                ?? throw new CryptographicException("xenc:EncryptedKey is not for any of our certificates.");
            var ourCertificate = ours.FirstOrDefault(c => c.Thumbprint == recipient.Thumbprint)
                ?? throw new CryptographicException("xenc:EncryptedKey is not for any of our certificates.");

            var sessionKey = UnwrapKey(encryptedKey, ourCertificate);

            var referenceList = SecurityTokenResolver.FirstChild(encryptedKey, "ReferenceList", WsSecurityNames.Xenc)
                ?? throw new CryptographicException("xenc:EncryptedKey has no xenc:ReferenceList.");
            foreach (var dataReference in Children(referenceList, "DataReference", WsSecurityNames.Xenc))
            {
                var uri = dataReference.GetAttribute("URI");
                if (!uri.StartsWith('#'))
                    throw new CryptographicException($"xenc:DataReference '{uri}' does not point into the message.");
                var encryptedData = SecurityTokenResolver.FindById(message.Envelope, uri[1..], "EncryptedData", WsSecurityNames.Xenc)
                    ?? throw new CryptographicException($"No xenc:EncryptedData carries the id '{uri[1..]}'.");
                decrypted.Add(DecryptAttachment(encryptedData, sessionKey, message));
            }
        }

        return decrypted;
    }

    private static byte[] UnwrapKey(XmlElement encryptedKey, X509Certificate2 ourCertificate)
    {
        var method = SecurityTokenResolver.FirstChild(encryptedKey, "EncryptionMethod", WsSecurityNames.Xenc)
            ?? throw new CryptographicException("xenc:EncryptedKey has no xenc:EncryptionMethod.");
        var algorithm = method.GetAttribute("Algorithm");
        if (!As4Algorithms.IsSupportedKeyTransport(algorithm))
            throw new CryptographicException($"Key transport '{algorithm}' is not supported.");

        // XML Encryption 1.1 RSA-OAEP names the digest and the mask generation function separately; .NET's
        // OAEP padding uses one hash for both, so only matching pairs are accepted.
        var digest = SecurityTokenResolver.FirstChild(method, "DigestMethod", WsSecurityNames.Ds)?.GetAttribute("Algorithm");
        var mgf = SecurityTokenResolver.FirstChild(method, "MGF", WsSecurityNames.Xenc11)?.GetAttribute("Algorithm");
        if (!As4Algorithms.IsSupportedDigest(digest) || !As4Algorithms.IsSupportedMaskGeneration(mgf))
            throw new CryptographicException($"RSA-OAEP with digest '{digest}' and MGF '{mgf}' is not supported.");

        var cipher = CipherValue(encryptedKey);
        using var rsa = ourCertificate.GetRSAPrivateKey()
            ?? throw new CryptographicException("Our decryption certificate has no RSA private key.");
        return rsa.Decrypt(cipher, RSAEncryptionPadding.OaepSHA256);
    }

    private static string DecryptAttachment(XmlElement encryptedData, byte[] sessionKey, SwaMessage message)
    {
        if (encryptedData.GetAttribute("Type") != WsSecurityNames.AttachmentContentOnly)
            throw new CryptographicException($"xenc:EncryptedData of type '{encryptedData.GetAttribute("Type")}' is not supported.");

        var method = SecurityTokenResolver.FirstChild(encryptedData, "EncryptionMethod", WsSecurityNames.Xenc)?.GetAttribute("Algorithm");
        if (!As4Algorithms.IsSupportedDataEncryption(method))
            throw new CryptographicException($"Data encryption '{method}' is not supported.");

        var cipherData = SecurityTokenResolver.FirstChild(encryptedData, "CipherData", WsSecurityNames.Xenc)
            ?? throw new CryptographicException("xenc:EncryptedData has no xenc:CipherData.");
        var cipherReference = SecurityTokenResolver.FirstChild(cipherData, "CipherReference", WsSecurityNames.Xenc)
            ?? throw new CryptographicException("xenc:EncryptedData of an attachment has no xenc:CipherReference.");
        var uri = cipherReference.GetAttribute("URI");
        if (!uri.StartsWith(WsSecurityNames.CidPrefix, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"xenc:CipherReference '{uri}' does not name a MIME part.");

        var id = WsSecurityNames.ContentId(uri);
        if (!message.Parts.TryGetValue(id, out var part))
            throw new CryptographicException($"The message has no MIME part with Content-ID '{id}'.");

        // GCM authenticates only at the end: the plaintext goes to a spool that is discarded unless the tag verifies.
        part.Replace(message.Spooled(output => AesGcmCipher.Decrypt(sessionKey, part.OpenRead(), output)));
        var mimeType = encryptedData.GetAttribute("MimeType");
        if (mimeType.Length > 0)
            part.ContentType = mimeType;
        return id;
    }

    // ── Encryption ───────────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts every attachment for <paramref name="recipient"/> with one fresh AES-128-GCM session key,
    /// transported by RSA-OAEP with SHA-256 and MGF1-SHA256 (eDelivery AS4 1.16). Each part's content becomes
    /// <c>IV || ciphertext || tag</c> and its type <c>application/octet-stream</c>; the original type is kept in
    /// <c>xenc:EncryptedData/@MimeType</c>. Call it after signing: signatures are over the plaintext. The key
    /// transport elements are placed first in the security header, as a WS-Security processor that prepends
    /// each step would leave them.
    /// </summary>
    public static void Encrypt(SwaMessage message, X509Certificate2 recipient, As4KeyReference keyReference)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipient);
        if (message.Parts.Count == 0)
            return;

        var doc = message.Envelope;
        var parts = SoapParts.Of(doc);
        var security = parts.Security ?? SoapParts.AddSecurityHeader(doc, parts.Header);

        var sessionKey = RandomNumberGenerator.GetBytes(16);
        using var rsa = recipient.GetRSAPublicKey() ?? throw new CryptographicException("The recipient certificate has no RSA key.");

        var encryptedKey = doc.CreateElement("xenc", "EncryptedKey", WsSecurityNames.Xenc);
        var keyId = "EK-" + Guid.NewGuid().ToString("N");
        encryptedKey.SetAttribute("Id", keyId);
        var keyMethod = As4Signature.Append(encryptedKey, "xenc", "EncryptionMethod", WsSecurityNames.Xenc);
        keyMethod.SetAttribute("Algorithm", As4Algorithms.RsaOaep);
        As4Signature.Append(keyMethod, "ds", "DigestMethod", WsSecurityNames.Ds).SetAttribute("Algorithm", As4Algorithms.Sha256);
        As4Signature.Append(keyMethod, "xenc11", "MGF", WsSecurityNames.Xenc11).SetAttribute("Algorithm", As4Algorithms.Mgf1Sha256);

        // The key transport block goes first; the recipient's token, if referenced by BST, before it.
        security.PrependChild(encryptedKey);
        encryptedKey.AppendChild(SecurityTokenResolver.CreateKeyInfo(doc, security, recipient, keyReference, insertTokenBefore: encryptedKey));
        var cipherData = As4Signature.Append(encryptedKey, "xenc", "CipherData", WsSecurityNames.Xenc);
        As4Signature.Append(cipherData, "xenc", "CipherValue", WsSecurityNames.Xenc).InnerText =
            Convert.ToBase64String(rsa.Encrypt(sessionKey, RSAEncryptionPadding.OaepSHA256));
        var referenceList = As4Signature.Append(encryptedKey, "xenc", "ReferenceList", WsSecurityNames.Xenc);

        XmlNode after = encryptedKey;
        foreach (var part in message.Parts.Values)
        {
            var dataId = "ED-" + Guid.NewGuid().ToString("N");
            As4Signature.Append(referenceList, "xenc", "DataReference", WsSecurityNames.Xenc).SetAttribute("URI", "#" + dataId);

            var encryptedData = doc.CreateElement("xenc", "EncryptedData", WsSecurityNames.Xenc);
            encryptedData.SetAttribute("Id", dataId);
            encryptedData.SetAttribute("MimeType", part.ContentType);
            encryptedData.SetAttribute("Type", WsSecurityNames.AttachmentContentOnly);
            As4Signature.Append(encryptedData, "xenc", "EncryptionMethod", WsSecurityNames.Xenc).SetAttribute("Algorithm", As4Algorithms.Aes128Gcm);

            var dataKeyInfo = As4Signature.Append(encryptedData, "ds", "KeyInfo", WsSecurityNames.Ds);
            var str = As4Signature.Append(dataKeyInfo, "wsse", "SecurityTokenReference", WsSecurityNames.Wsse);
            var tokenType = doc.CreateAttribute("wsse11", "TokenType", WsSecurityNames.Wsse11);
            tokenType.Value = "http://docs.oasis-open.org/wss/oasis-wss-soap-message-security-1.1#EncryptedKey";
            str.Attributes.Append(tokenType);
            As4Signature.Append(str, "wsse", "Reference", WsSecurityNames.Wsse).SetAttribute("URI", "#" + keyId);

            var dataCipher = As4Signature.Append(encryptedData, "xenc", "CipherData", WsSecurityNames.Xenc);
            var cipherReference = As4Signature.Append(dataCipher, "xenc", "CipherReference", WsSecurityNames.Xenc);
            cipherReference.SetAttribute("URI", WsSecurityNames.CidUri(part.ContentId));
            var transforms = As4Signature.Append(cipherReference, "xenc", "Transforms", WsSecurityNames.Xenc);
            As4Signature.Append(transforms, "ds", "Transform", WsSecurityNames.Ds).SetAttribute("Algorithm", WsSecurityNames.AttachmentCiphertextTransform);

            security.InsertAfter(encryptedData, after);
            after = encryptedData;

            part.Replace(message.Spooled(output => AesGcmCipher.Encrypt(sessionKey, part.OpenRead(), output)));
            part.ContentType = "application/octet-stream";
        }
    }

    // ── Signature ────────────────────────────────────────────────────────────

    /// <summary>Verifies the message signature; see <see cref="As4Signature.Verify"/>.</summary>
    public static SignatureVerification Verify(SwaMessage message, IReadOnlyCollection<X509Certificate2> partnerCertificates,
        TimeSpan timestampTolerance, DateTimeOffset now, RevocationPolicy? revocation = null, TimeSpan? timestampTimeToLive = null) =>
        As4Signature.Verify(message, partnerCertificates, timestampTolerance, now, revocation, timestampTimeToLive);

    /// <summary>Signs the message; see <see cref="As4Signature.Sign"/>.</summary>
    public static IReadOnlyList<XmlElement> Sign(SwaMessage message, X509Certificate2 signer, As4KeyReference keyReference,
        TimeSpan? timestampTtl, DateTimeOffset now) =>
        As4Signature.Sign(message, signer, keyReference, timestampTtl, now);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static XmlElement SecurityHeader(XmlDocument envelope)
    {
        var root = envelope.DocumentElement ?? throw new CryptographicException("The envelope is empty.");
        var header = SecurityTokenResolver.FirstChild(root, "Header", WsSecurityNames.Soap12)
            ?? throw new CryptographicException("The envelope has no SOAP 1.2 header.");
        var securities = Children(header, "Security", WsSecurityNames.Wsse).ToList();
        return securities.Count == 1
            ? securities[0]
            : throw new CryptographicException($"The SOAP header carries {securities.Count} wsse:Security headers, not one.");
    }

    private static IEnumerable<XmlElement> Children(XmlElement parent, string localName, string ns) =>
        parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == localName && e.NamespaceURI == ns);

    private static byte[] CipherValue(XmlElement encrypted)
    {
        var cipherData = SecurityTokenResolver.FirstChild(encrypted, "CipherData", WsSecurityNames.Xenc)
            ?? throw new CryptographicException($"xenc:{encrypted.LocalName} has no xenc:CipherData.");
        var value = SecurityTokenResolver.FirstChild(cipherData, "CipherValue", WsSecurityNames.Xenc)
            ?? throw new CryptographicException($"xenc:{encrypted.LocalName} has no xenc:CipherValue.");
        return Convert.FromBase64String(value.InnerText.Trim());
    }
}
