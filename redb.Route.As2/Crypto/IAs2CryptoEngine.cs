using System.Security.Cryptography.X509Certificates;
using MimeKit;
using MimeKit.Cryptography;

namespace redb.Route.As2.Crypto;

/// <summary>
/// S/MIME operations an AS2 connector needs: sign, encrypt, compress and their inverses, plus the AS2
/// Message Integrity Check. Kept behind an interface so the crypto backend is swappable (default:
/// <see cref="As2CryptoEngine"/> on MimeKit / Bouncy Castle — the industry AS2 foundation; escape hatch:
/// Bouncy Castle .NET directly). Works on MimeKit's <see cref="MimeEntity"/> MIME model. See
/// <c>docs/as2/02-DESIGN.md §5</c>.
/// </summary>
internal interface IAs2CryptoEngine
{
    /// <summary>Signs <paramref name="content"/> into a <c>multipart/signed</c> with our certificate.</summary>
    MultipartSigned Sign(MimeEntity content, X509Certificate2 signerCert, string micalg);

    /// <summary>Encrypts <paramref name="content"/> to the partner's certificate (S/MIME enveloped-data).</summary>
    MimeEntity Encrypt(MimeEntity content, X509Certificate2 recipientCert, string encAlg);

    /// <summary>Compresses <paramref name="content"/> (RFC 3274 compressed-data).</summary>
    MimeEntity Compress(MimeEntity content);

    /// <summary>Decrypts an S/MIME enveloped-data entity with our private key.</summary>
    MimeEntity Decrypt(MimeEntity encrypted, X509Certificate2 ourCert);

    /// <summary>
    /// Decrypts an S/MIME enveloped-data entity with our private key, writing the decrypted MIME entity to
    /// <paramref name="output"/> (a spool) instead of building it in memory.
    /// </summary>
    void DecryptTo(ApplicationPkcs7Mime encrypted, X509Certificate2 ourCert, Stream output);

    /// <summary>Decompresses an RFC 3274 compressed-data entity into <paramref name="output"/> (a spool).</summary>
    void DecompressTo(ApplicationPkcs7Mime compressed, Stream output);

    /// <summary>Verifies a <c>multipart/signed</c> against the partner's certificate.</summary>
    bool Verify(MultipartSigned signed, X509Certificate2 signerCert);

    /// <summary>Decompresses an RFC 3274 compressed-data entity.</summary>
    MimeEntity Decompress(MimeEntity compressed);

    /// <summary>
    /// Computes the AS2 MIC over <paramref name="part"/> per RFC 4130 §7.3.1. When
    /// <paramref name="includeHeaders"/> is true (signed / encrypted messages) the MIME headers and content
    /// are hashed together, CRLF-canonicalized; when false (plain messages) only the content is hashed.
    /// </summary>
    As2Mic ComputeMic(MimeEntity part, string micalg, bool includeHeaders);

    /// <summary>
    /// The MIC of a RECEIVED signed part (RFC 4130 §7.3.1): its MIME headers and content exactly as they arrived, with
    /// no re-encoding. The sending side prepares a part before it signs it; the receiving side hashes what was signed.
    /// </summary>
    As2Mic ComputeReceivedMic(MimeEntity part, string micalg);
}
