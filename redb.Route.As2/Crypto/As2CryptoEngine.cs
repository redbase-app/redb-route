using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MimeKit;
using MimeKit.Cryptography;

namespace redb.Route.As2.Crypto;

/// <summary>
/// Default AS2 crypto engine, built on MimeKit's S/MIME support (Bouncy Castle underneath) — the same
/// cryptographic foundation the AS2 industry (camel-as2, OpenAS2, Mendelson) interoperates on.
/// Uses an in-memory <see cref="TemporarySecureMimeContext"/> per operation with the relevant certificate
/// imported, so no on-disk certificate store is required. See <c>docs/as2/02-DESIGN.md §5</c>.
/// </summary>
internal sealed class As2CryptoEngine : IAs2CryptoEngine
{
    /// <inheritdoc />
    public MultipartSigned Sign(MimeEntity content, X509Certificate2 signerCert, string micalg)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(signerCert);
        if (!signerCert.HasPrivateKey)
            throw new ArgumentException("Signing certificate must have a private key.", nameof(signerCert));

        using var ctx = new TemporarySecureMimeContext();
        var signer = new CmsSigner(signerCert) { DigestAlgorithm = MapDigest(micalg) };
        return MultipartSigned.Create(ctx, signer, content);
    }

    /// <inheritdoc />
    public MimeEntity Encrypt(MimeEntity content, X509Certificate2 recipientCert, string encAlg)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(recipientCert);

        using var ctx = new TemporarySecureMimeContext();
        var recipient = new CmsRecipient(recipientCert) { EncryptionAlgorithms = [MapEncryption(encAlg)] };
        return ApplicationPkcs7Mime.Encrypt(ctx, new CmsRecipientCollection { recipient }, content);
    }

    /// <inheritdoc />
    public MimeEntity Compress(MimeEntity content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var ctx = new TemporarySecureMimeContext();
        return ApplicationPkcs7Mime.Compress(ctx, content);
    }

    /// <inheritdoc />
    public MimeEntity Decrypt(MimeEntity encrypted, X509Certificate2 ourCert)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        ArgumentNullException.ThrowIfNull(ourCert);
        if (encrypted is not ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.EnvelopedData } pkcs7)
            throw new ArgumentException("Entity is not S/MIME enveloped-data.", nameof(encrypted));

        using var ctx = new TemporarySecureMimeContext();
        ctx.Import(ourCert);
        return pkcs7.Decrypt(ctx);
    }

    /// <inheritdoc />
    public bool Verify(MultipartSigned signed, X509Certificate2 signerCert)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(signerCert);

        using var ctx = new TemporarySecureMimeContext();
        ctx.Import(signerCert);
        try
        {
            var signatures = signed.Verify(ctx);
            if (signatures.Count == 0)
                return false;

            foreach (var signature in signatures)
            {
                // The signature must be cryptographically valid AND made by the expected partner
                // certificate — an S/MIME signature embeds its own signer cert, so crypto validity alone
                // does not prove *who* signed. Bind it to signerCert by fingerprint.
                if (!signature.Verify(verifySignatureOnly: true))
                    return false;

                var fingerprint = signature.SignerCertificate?.Fingerprint;
                if (fingerprint is null || !FingerprintMatches(fingerprint, signerCert))
                    return false;
            }
            return true;
        }
        catch (DigitalSignatureVerifyException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public MimeEntity Decompress(MimeEntity compressed)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (compressed is not ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.CompressedData } pkcs7)
            throw new ArgumentException("Entity is not S/MIME compressed-data.", nameof(compressed));

        using var ctx = new TemporarySecureMimeContext();
        return pkcs7.Decompress(ctx);
    }

    /// <inheritdoc />
    public As2Mic ComputeMic(MimeEntity part, string micalg, bool includeHeaders)
    {
        ArgumentNullException.ThrowIfNull(part);

        using var stream = new MemoryStream();
        if (includeHeaders)
        {
            // Signed / encrypted: hash the MIME headers + content, CRLF-canonicalized (RFC 4130 §7.3.1).
            var options = FormatOptions.Default.Clone();
            options.NewLineFormat = NewLineFormat.Dos;
            part.Prepare(EncodingConstraint.SevenBit);
            part.WriteTo(options, stream);
        }
        else if (part is MimePart { Content: not null } mp)
        {
            // Plain: hash the decoded content only, without MIME headers.
            mp.Content.DecodeTo(stream);
        }
        else
        {
            part.WriteTo(stream);
        }

        stream.Position = 0;
        using var hasher = CreateHash(micalg);
        var digest = hasher.ComputeHash(stream);
        return new As2Mic(Convert.ToBase64String(digest), NormalizeAlg(micalg));
    }

    /// <summary>
    /// True if MimeKit's signer fingerprint matches <paramref name="cert"/>. MimeKit reports the SHA-1
    /// fingerprint as hex; .NET's <see cref="X509Certificate2.Thumbprint"/> is the same SHA-1 hash in a
    /// different case, so we normalize both to lowercase hex (and fall back to hashing the raw cert).
    /// </summary>
    private static bool FingerprintMatches(string mimeKitFingerprint, X509Certificate2 cert)
    {
        static string Norm(string s) => string.Concat(s.Where(Uri.IsHexDigit)).ToLowerInvariant();

        var actual = Norm(mimeKitFingerprint);
        if (actual == Norm(cert.Thumbprint))
            return true;

        var sha1 = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA1)).ToLowerInvariant();
        return actual == sha1;
    }

    /// <summary>Whether <paramref name="alg"/> is a signature digest this engine supports.</summary>
    public static bool IsSupportedDigest(string alg)
        => Normalize(alg) is "sha-256" or "sha-1" or "sha-384" or "sha-512";

    /// <summary>Whether <paramref name="alg"/> is an encryption algorithm this engine supports.</summary>
    public static bool IsSupportedEncryption(string alg) => alg.ToLowerInvariant() is
        "aes-128-cbc" or "aes128" or "aes-192-cbc" or "aes192" or "aes-256-cbc" or "aes256" or "3des" or "des-ede3-cbc";

    // ── Algorithm mapping ────────────────────────────────────────────────────

    private static DigestAlgorithm MapDigest(string micalg) => Normalize(micalg) switch
    {
        "sha-256" => DigestAlgorithm.Sha256,
        "sha-1" => DigestAlgorithm.Sha1,
        "sha-384" => DigestAlgorithm.Sha384,
        "sha-512" => DigestAlgorithm.Sha512,
        _ => throw new NotSupportedException($"Unsupported signature digest algorithm '{micalg}'."),
    };

    private static EncryptionAlgorithm MapEncryption(string alg) => alg.ToLowerInvariant() switch
    {
        "aes-128-cbc" or "aes128" => EncryptionAlgorithm.Aes128,
        "aes-192-cbc" or "aes192" => EncryptionAlgorithm.Aes192,
        "aes-256-cbc" or "aes256" => EncryptionAlgorithm.Aes256,
        "3des" or "des-ede3-cbc" => EncryptionAlgorithm.TripleDes,
        _ => throw new NotSupportedException($"Unsupported encryption algorithm '{alg}'."),
    };

    private static HashAlgorithm CreateHash(string micalg) => Normalize(micalg) switch
    {
        "sha-256" => SHA256.Create(),
        "sha-1" => SHA1.Create(),
        "sha-384" => SHA384.Create(),
        "sha-512" => SHA512.Create(),
        _ => throw new NotSupportedException($"Unsupported MIC algorithm '{micalg}'."),
    };

    /// <summary>Canonical AS2 micalg spelling (e.g. <c>sha-256</c>).</summary>
    private static string NormalizeAlg(string micalg) => Normalize(micalg);

    private static string Normalize(string alg) => alg.ToLowerInvariant() switch
    {
        "sha256" or "sha-256" => "sha-256",
        "sha1" or "sha-1" => "sha-1",
        "sha384" or "sha-384" => "sha-384",
        "sha512" or "sha-512" => "sha-512",
        _ => alg.ToLowerInvariant(),
    };
}
