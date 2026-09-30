using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using redb.Route.As4.Mime;

namespace redb.Route.As4.Security;

/// <summary>
/// XML Signature as the AS4 profile uses it, without <c>System.Security.Cryptography.Xml.SignedXml</c>:
/// <c>SignedXml</c> refuses every reference that is not a same-document <c>#id</c>, so it cannot digest the
/// <c>cid:</c> references to attachments (dotnet/runtime, <c>Reference.CalculateHashValue</c>). Canonicalization
/// is the .NET exclusive c14n transform (<see cref="ExclusiveCanonicalizer"/>); the rest — reference
/// processing and the RSA signature over the canonical <c>ds:SignedInfo</c> — is here, restricted to what the
/// eDelivery AS4 1.16 common profile uses: exclusive c14n, SHA-256, RSA-SHA256, the SwA
/// Attachment-Content-Signature-Transform. Anything else is refused, not interpreted.
/// </summary>
internal static class As4Signature
{
    // ── Verification ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the single <c>ds:Signature</c> of the security header over the current message content (after
    /// decryption), resolves the signer from its key reference, accepts it only if it is one of
    /// <paramref name="trustedSigners"/>, and checks that the signature covers <c>eb:Messaging</c>, the SOAP
    /// body, every attachment and the <c>wsu:Timestamp</c> when there is one — whose <c>Expires</c> must not be
    /// in the past beyond <paramref name="timestampTolerance"/>. Throws <see cref="CryptographicException"/> on
    /// any failure.
    /// </summary>
    public static SignatureVerification Verify(SwaMessage message, IReadOnlyCollection<X509Certificate2> trustedSigners,
        TimeSpan timestampTolerance, DateTimeOffset now, RevocationPolicy? revocation = null, TimeSpan? timestampTimeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var envelope = message.Envelope;
        EnsureUniqueIds(envelope);

        var parts = SoapParts.Of(envelope);
        var security = parts.Security ?? throw new CryptographicException("The message carries no wsse:Security header.");
        var signature = Children(security, "Signature", WsSecurityNames.Ds).ToList() is { Count: 1 } one
            ? one[0]
            : throw new CryptographicException("The security header does not carry exactly one ds:Signature.");

        var signedInfo = Child(signature, "SignedInfo", WsSecurityNames.Ds)
            ?? throw new CryptographicException("ds:Signature has no ds:SignedInfo.");
        var c14nMethod = Child(signedInfo, "CanonicalizationMethod", WsSecurityNames.Ds)
            ?? throw new CryptographicException("ds:SignedInfo has no ds:CanonicalizationMethod.");
        if (c14nMethod.GetAttribute("Algorithm") != WsSecurityNames.ExcC14N)
            throw new CryptographicException($"Canonicalization '{c14nMethod.GetAttribute("Algorithm")}' is not supported.");
        var signatureMethod = Child(signedInfo, "SignatureMethod", WsSecurityNames.Ds)?.GetAttribute("Algorithm");
        if (!As4Algorithms.IsSupportedSignature(signatureMethod))
            throw new CryptographicException($"Signature method '{signatureMethod}' is not supported.");

        var keyInfo = Child(signature, "KeyInfo", WsSecurityNames.Ds)
            ?? throw new CryptographicException("ds:Signature has no ds:KeyInfo.");
        var signer = SecurityTokenResolver.Resolve(keyInfo, envelope, trustedSigners);
        if (signer is null || !trustedSigners.Any(c => c.Thumbprint == signer.Thumbprint))
            throw new CryptographicException("The signing certificate is not one of the partner's.");
        CertificateValidity.EnsureSigner(signer, now, revocation ?? RevocationPolicy.None);

        var signatureValue = Convert.FromBase64String(
            (Child(signature, "SignatureValue", WsSecurityNames.Ds)
             ?? throw new CryptographicException("ds:Signature has no ds:SignatureValue.")).InnerText.Trim());

        var canonicalSignedInfo = ExclusiveCanonicalizer.Canonicalize(signedInfo, ExclusiveCanonicalizer.PrefixList(c14nMethod));
        using (var rsa = signer.GetRSAPublicKey() ?? throw new CryptographicException("The signing certificate has no RSA key."))
        {
            if (!rsa.VerifyData(canonicalSignedInfo, signatureValue, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("The signature does not verify.");
        }

        var references = Children(signedInfo, "Reference", WsSecurityNames.Ds).ToList();
        if (references.Count == 0)
            throw new CryptographicException("ds:SignedInfo has no ds:Reference.");

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var uri = reference.GetAttribute("URI");
            if (!covered.Add(uri))
                throw new CryptographicException($"ds:Reference '{uri}' appears twice.");

            var digestMethod = Child(reference, "DigestMethod", WsSecurityNames.Ds)?.GetAttribute("Algorithm");
            if (!As4Algorithms.IsSupportedDigest(digestMethod))
                throw new CryptographicException($"Digest method '{digestMethod}' is not supported.");
            var expected = Convert.FromBase64String(
                (Child(reference, "DigestValue", WsSecurityNames.Ds)
                 ?? throw new CryptographicException($"ds:Reference '{uri}' has no ds:DigestValue.")).InnerText.Trim());

            var actual = ReferenceDigest(reference, uri, message);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new CryptographicException($"The digest of '{uri}' does not match.");
        }

        EnsureCoverage(parts, message, covered, timestampTolerance, timestampTimeToLive ?? As4Partner.DefaultTimestampTimeToLive, now);

        var copies = references.Select(r => (XmlElement)r.CloneNode(deep: true)).ToList();
        return new SignatureVerification(signer, copies);
    }

    /// <summary>SHA-256 of what a reference covers: a canonicalized element, or an attachment read as a stream.</summary>
    private static byte[] ReferenceDigest(XmlElement reference, string uri, SwaMessage message)
    {
        var transforms = Child(reference, "Transforms", WsSecurityNames.Ds) is { } list
            ? Children(list, "Transform", WsSecurityNames.Ds).ToList()
            : [];
        if (transforms.Count != 1)
            throw new CryptographicException($"ds:Reference '{uri}' must carry exactly one transform.");
        var transform = transforms[0];
        var algorithm = transform.GetAttribute("Algorithm");

        if (uri.StartsWith('#'))
        {
            if (algorithm != WsSecurityNames.ExcC14N)
                throw new CryptographicException($"Transform '{algorithm}' of '{uri}' is not supported.");
            var target = FindById(message.Envelope, uri[1..])
                ?? throw new CryptographicException($"No element carries the id '{uri[1..]}'.");
            return SHA256.HashData(ExclusiveCanonicalizer.Canonicalize(target, ExclusiveCanonicalizer.PrefixList(transform)));
        }

        if (uri.StartsWith(WsSecurityNames.CidPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (algorithm != WsSecurityNames.AttachmentContentSignatureTransform)
                throw new CryptographicException($"Transform '{algorithm}' of '{uri}' is not supported.");
            var id = WsSecurityNames.ContentId(uri);
            return message.Parts.TryGetValue(id, out var part)
                ? AttachmentCanonicalizer.Sha256(part)
                : throw new CryptographicException($"The message has no MIME part with Content-ID '{id}'.");
        }

        throw new CryptographicException($"ds:Reference '{uri}' points outside the message.");
    }

    private static void EnsureCoverage(SoapParts parts, SwaMessage message, HashSet<string> covered,
        TimeSpan timestampTolerance, TimeSpan timestampTimeToLive, DateTimeOffset now)
    {
        bool Covers(XmlElement element) => covered.Contains("#" + WsuId(element));

        if (parts.Messaging is null || !Covers(parts.Messaging))
            throw new CryptographicException("The signature does not cover eb:Messaging.");
        if (!Covers(parts.Body))
            throw new CryptographicException("The signature does not cover the SOAP body.");
        foreach (var id in message.Parts.Keys)
        {
            if (!covered.Contains(WsSecurityNames.CidUri(id)))
                throw new CryptographicException($"The signature does not cover the attachment '{id}'.");
        }

        if (parts.Timestamp is { } timestamp)
        {
            if (!Covers(timestamp))
                throw new CryptographicException("The signature does not cover wsu:Timestamp.");
            CheckTimestamp(timestamp, timestampTolerance, timestampTimeToLive, now);
        }
    }

    /// <summary>
    /// A received <c>wsu:Timestamp</c>, checked as WSS4J checks it (the library Domibus and Holodeck verify with):
    /// <c>Created</c> is required (BSP R3203) and may lie neither in the future beyond <paramref name="tolerance"/>
    /// (WSS4J futureTimeToLive) nor further back than <paramref name="timeToLive"/> (WSS4J timeStampTTL);
    /// <c>Expires</c> is optional and, when present, must not have passed beyond <paramref name="tolerance"/>. An
    /// <c>Expires</c> far ahead buys nothing: the age is bounded by <c>Created</c>.
    /// </summary>
    internal static void CheckTimestamp(XmlElement timestamp, TimeSpan tolerance, TimeSpan timeToLive, DateTimeOffset now)
    {
        var created = Instant(timestamp, "Created")
            ?? throw new CryptographicException("wsu:Timestamp has no wsu:Created.");
        if (created > now + tolerance)
            throw new CryptographicException("wsu:Timestamp was created in the future.");
        if (created < now - timeToLive)
            throw new CryptographicException("wsu:Timestamp was created too long ago.");
        if (Instant(timestamp, "Expires") is { } expires && expires + tolerance < now)
            throw new CryptographicException("wsu:Timestamp has expired.");

        static DateTimeOffset? Instant(XmlElement timestamp, string name)
        {
            if (Child(timestamp, name, WsSecurityNames.Wsu)?.InnerText.Trim() is not { } text)
                return null;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
                ? value
                : throw new CryptographicException($"wsu:{name} is not a date and time.");
        }
    }

    // ── Signing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Signs <c>eb:Messaging</c>, the SOAP body, every attachment and — when <paramref name="timestampTtl"/> is
    /// given — a new <c>wsu:Timestamp</c>, adding the signature to the security header (created when absent).
    /// Call it before encryption: attachment digests are over the plaintext. Returns copies of the
    /// <c>ds:Reference</c> elements: what a non-repudiation receipt must echo back.
    /// </summary>
    public static IReadOnlyList<XmlElement> Sign(SwaMessage message, X509Certificate2 signer, As4KeyReference keyReference,
        TimeSpan? timestampTtl, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signer);
        if (!signer.HasPrivateKey)
            throw new CryptographicException("The signing certificate has no private key.");

        var doc = message.Envelope;
        var parts = SoapParts.Of(doc);
        if (parts.Messaging is null)
            throw new CryptographicException("The SOAP header has no eb:Messaging to sign.");
        var security = parts.Security ?? SoapParts.AddSecurityHeader(doc, parts.Header);
        if (Children(security, "Signature", WsSecurityNames.Ds).Any())
            throw new CryptographicException("The message is signed already.");

        var targets = new List<XmlElement> { parts.Messaging, parts.Body };
        if (timestampTtl is { } ttl)
        {
            if (parts.Timestamp is not null)
                throw new CryptographicException("The security header carries a wsu:Timestamp already.");
            targets.Add(AddTimestamp(doc, security, now, ttl));
        }
        foreach (var target in targets)
        {
            EnsureWsuId(target);
            ExclusiveCanonicalizer.DeclareNamespaces(target);
        }

        var signature = doc.CreateElement("ds", "Signature", WsSecurityNames.Ds);
        signature.SetAttribute("Id", "SIG-" + Guid.NewGuid().ToString("N"));
        var signedInfo = Append(signature, "ds", "SignedInfo", WsSecurityNames.Ds);
        Append(signedInfo, "ds", "CanonicalizationMethod", WsSecurityNames.Ds).SetAttribute("Algorithm", WsSecurityNames.ExcC14N);
        Append(signedInfo, "ds", "SignatureMethod", WsSecurityNames.Ds).SetAttribute("Algorithm", As4Algorithms.RsaSha256);

        foreach (var target in targets)
            AppendReference(signedInfo, "#" + WsuId(target), WsSecurityNames.ExcC14N,
                SHA256.HashData(ExclusiveCanonicalizer.Canonicalize(target, null)));
        foreach (var part in message.Parts.Values)
            AppendReference(signedInfo, WsSecurityNames.CidUri(part.ContentId), WsSecurityNames.AttachmentContentSignatureTransform,
                AttachmentCanonicalizer.Sha256(part));

        // Token first, signature after it: a receiver meets the token before the reference to it.
        var keyInfo = SecurityTokenResolver.CreateKeyInfo(doc, security, signer, keyReference);
        security.AppendChild(signature);

        ExclusiveCanonicalizer.DeclareNamespaces(security);
        // Canonicalize SignedInfo where it now stands, so the namespaces in scope are the final ones.
        var canonical = ExclusiveCanonicalizer.Canonicalize(signedInfo, null);
        using var rsa = signer.GetRSAPrivateKey() ?? throw new CryptographicException("The signing certificate has no RSA private key.");
        var value = rsa.SignData(canonical, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Append(signature, "ds", "SignatureValue", WsSecurityNames.Ds).InnerText = Convert.ToBase64String(value);
        signature.AppendChild(keyInfo);

        return Children(signedInfo, "Reference", WsSecurityNames.Ds).Select(r => (XmlElement)r.CloneNode(deep: true)).ToList();
    }

    private static void AppendReference(XmlElement signedInfo, string uri, string transform, byte[] digest)
    {
        var reference = Append(signedInfo, "ds", "Reference", WsSecurityNames.Ds);
        reference.SetAttribute("URI", uri);
        var transforms = Append(reference, "ds", "Transforms", WsSecurityNames.Ds);
        Append(transforms, "ds", "Transform", WsSecurityNames.Ds).SetAttribute("Algorithm", transform);
        Append(reference, "ds", "DigestMethod", WsSecurityNames.Ds).SetAttribute("Algorithm", As4Algorithms.Sha256);
        Append(reference, "ds", "DigestValue", WsSecurityNames.Ds).InnerText = Convert.ToBase64String(digest);
    }

    private static XmlElement AddTimestamp(XmlDocument doc, XmlElement security, DateTimeOffset now, TimeSpan ttl)
    {
        var timestamp = doc.CreateElement("wsu", "Timestamp", WsSecurityNames.Wsu);
        Append(timestamp, "wsu", "Created", WsSecurityNames.Wsu).InnerText = Format(now);
        Append(timestamp, "wsu", "Expires", WsSecurityNames.Wsu).InnerText = Format(now + ttl);
        security.PrependChild(timestamp);
        return timestamp;

        static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    // ── Ids ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses a document in which one id names two elements. Checked over the whole envelope before any
    /// reference is followed: that is the shape of a signature-wrapping attack.
    /// </summary>
    public static void EnsureUniqueIds(XmlDocument envelope)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlElement element in envelope.GetElementsByTagName("*"))
        {
            foreach (var id in new[] { element.GetAttribute("Id", WsSecurityNames.Wsu), element.GetAttribute("Id") })
            {
                if (id.Length > 0 && !seen.Add(id))
                    throw new CryptographicException($"Two elements carry the id '{id}'.");
            }
        }
    }

    /// <summary>The element with <c>wsu:Id</c> or <c>Id</c> <paramref name="id"/>; ids are unique (<see cref="EnsureUniqueIds"/>).</summary>
    public static XmlElement? FindById(XmlDocument document, string id)
    {
        foreach (XmlElement element in document.GetElementsByTagName("*"))
        {
            if (element.GetAttribute("Id", WsSecurityNames.Wsu) == id || element.GetAttribute("Id") == id)
                return element;
        }
        return null;
    }

    private static string WsuId(XmlElement element) => element.GetAttribute("Id", WsSecurityNames.Wsu);

    private static void EnsureWsuId(XmlElement element)
    {
        if (WsuId(element).Length > 0) return;
        var attribute = element.OwnerDocument.CreateAttribute("wsu", "Id", WsSecurityNames.Wsu);
        attribute.Value = "id-" + Guid.NewGuid().ToString("N");
        element.Attributes.Append(attribute);
    }

    // ── XML helpers ──────────────────────────────────────────────────────────

    internal static XmlElement Append(XmlElement parent, string prefix, string localName, string ns)
    {
        var element = parent.OwnerDocument.CreateElement(prefix, localName, ns);
        parent.AppendChild(element);
        return element;
    }

    internal static XmlElement? Child(XmlElement parent, string localName, string ns) =>
        parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == localName && e.NamespaceURI == ns);

    internal static IEnumerable<XmlElement> Children(XmlElement parent, string localName, string ns) =>
        parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == localName && e.NamespaceURI == ns);
}

/// <summary>Outcome of a successful signature verification.</summary>
/// <param name="Signer">The partner certificate that signed the message.</param>
/// <param name="References">
/// Copies of the <c>ds:Reference</c> elements of the signature — what a non-repudiation receipt returns, and
/// what a sender compares a receipt against.
/// </param>
internal sealed record SignatureVerification(X509Certificate2 Signer, IReadOnlyList<XmlElement> References);
