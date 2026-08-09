using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using MimeKit;
using MimeKit.Cryptography;
using redb.Route.As2.Crypto;

namespace redb.Route.Tests.As2.Crypto;

/// <summary>
/// Ф1 crypto tests: S/MIME sign/encrypt/compress round-trips across every AS2 profile, MIC determinism
/// (RFC 4130 §7.3.1), and negative cases. Certificates are generated in-process — no PFX files in the repo.
/// These prove self-consistency; interop against real AS2 implementations is Ф8.
/// </summary>
public class As2CryptoEngineTests
{
    private static readonly X509Certificate2 OurCert = MakeCert("redb-as2-us");
    private static readonly X509Certificate2 PartnerCert = MakeCert("redb-as2-partner");
    private readonly As2CryptoEngine _engine = new();

    private static MimeEntity Doc(string text = "ISA*00*          *00*          *ZZ*US~") => new TextPart("plain") { Text = text };

    private static string TextOf(MimeEntity e) => ((TextPart)e).Text!;

    // ── Sign / Verify ────────────────────────────────────────────────────────

    [Fact]
    public void Sign_ThenVerify_SameCert_True()
    {
        var signed = _engine.Sign(Doc(), OurCert, "sha-256");
        _engine.Verify(signed, OurCert).Should().BeTrue();
    }

    [Fact]
    public void Sign_ThenVerify_WrongSigner_False()
    {
        // Signed by us, but verified as if it should come from the partner → signer fingerprint mismatch.
        var signed = _engine.Sign(Doc(), OurCert, "sha-256");
        _engine.Verify(signed, PartnerCert).Should().BeFalse();
    }

    // ── Encrypt / Decrypt ────────────────────────────────────────────────────

    [Fact]
    public void Encrypt_ThenDecrypt_RestoresContent()
    {
        var encrypted = _engine.Encrypt(Doc("secret-edi"), PartnerCert, "aes-128-cbc");
        var decrypted = _engine.Decrypt(encrypted, PartnerCert);
        TextOf(decrypted).Should().Be("secret-edi");
    }

    // ── Compress / Decompress ────────────────────────────────────────────────

    [Fact]
    public void Compress_ThenDecompress_RestoresContent()
    {
        var compressed = _engine.Compress(Doc("compress-me"));
        var restored = _engine.Decompress(compressed);
        TextOf(restored).Should().Be("compress-me");
    }

    // ── Full profiles ────────────────────────────────────────────────────────

    [Fact]
    public void SignAndEncrypt_ThenDecryptAndVerify()
    {
        var signed = _engine.Sign(Doc("po-9001"), OurCert, "sha-256");
        var encrypted = _engine.Encrypt(signed, PartnerCert, "aes-256-cbc");

        var decrypted = _engine.Decrypt(encrypted, PartnerCert);
        decrypted.Should().BeAssignableTo<MultipartSigned>();
        _engine.Verify((MultipartSigned)decrypted, OurCert).Should().BeTrue();
    }

    [Fact]
    public void CompressSignEncrypt_FullRoundTrip()
    {
        var compressed = _engine.Compress(Doc("full-profile"));
        var signed = _engine.Sign(compressed, OurCert, "sha-256");
        var encrypted = _engine.Encrypt(signed, PartnerCert, "aes-128-cbc");

        var decrypted = _engine.Decrypt(encrypted, PartnerCert);
        decrypted.Should().BeAssignableTo<MultipartSigned>();
        _engine.Verify((MultipartSigned)decrypted, OurCert).Should().BeTrue();

        // Extract the signed payload (first part of multipart/signed) and decompress it.
        var inner = ((MultipartSigned)decrypted)[0];
        var restored = _engine.Decompress(inner);
        TextOf(restored).Should().Be("full-profile");
    }

    // ── MIC (RFC 4130 §7.3.1) ────────────────────────────────────────────────

    [Fact]
    public void ComputeMic_IsDeterministic()
    {
        var a = _engine.ComputeMic(Doc("same"), "sha-256", includeHeaders: true);
        var b = _engine.ComputeMic(Doc("same"), "sha-256", includeHeaders: true);
        a.Matches(b).Should().BeTrue();
        a.Algorithm.Should().Be("sha-256");
    }

    [Fact]
    public void ComputeMic_DifferentContent_DiffersAndParses()
    {
        var a = _engine.ComputeMic(Doc("one"), "sha-256", includeHeaders: true);
        var b = _engine.ComputeMic(Doc("two"), "sha-256", includeHeaders: true);
        a.Matches(b).Should().BeFalse();

        As2Mic.Parse(a.ToString()).Matches(a).Should().BeTrue();
    }

    [Fact]
    public void ComputeMic_IncludeHeaders_DiffersFromContentOnly()
    {
        var withHeaders = _engine.ComputeMic(Doc("body"), "sha-256", includeHeaders: true);
        var contentOnly = _engine.ComputeMic(Doc("body"), "sha-256", includeHeaders: false);
        withHeaders.Matches(contentOnly).Should().BeFalse();
    }

    // ── Algorithm matrix (Ф6) ────────────────────────────────────────────────

    [Theory]
    [InlineData("sha-1", "3des", false)]
    [InlineData("sha-256", "aes-128-cbc", false)]
    [InlineData("sha-256", "aes-256-cbc", true)]
    [InlineData("sha-384", "aes-192-cbc", true)]
    [InlineData("sha-512", "aes-256-cbc", false)]
    public void Matrix_CompressSignEncrypt_RoundTripsWithMic(string signAlg, string encAlg, bool compress)
    {
        MimeEntity entity = Doc("matrix-payload");
        if (compress) entity = _engine.Compress(entity);

        var micSent = _engine.ComputeMic(entity, signAlg, includeHeaders: true);
        var signed = _engine.Sign(entity, OurCert, signAlg);
        var encrypted = _engine.Encrypt(signed, PartnerCert, encAlg);

        var decrypted = _engine.Decrypt(encrypted, PartnerCert);
        _engine.Verify((MultipartSigned)decrypted, OurCert).Should().BeTrue();

        var inner = ((MultipartSigned)decrypted)[0];
        _engine.ComputeMic(inner, signAlg, includeHeaders: true).Matches(micSent).Should().BeTrue();

        if (compress) inner = _engine.Decompress(inner);
        TextOf(inner).Should().Be("matrix-payload");
    }

    // ── Cert generation ──────────────────────────────────────────────────────

    private static X509Certificate2 MakeCert(string cn)
    {
        // The RSA from RSA.Create() is an exportable, in-memory software key. Keep THIS key on the cert via
        // CopyWithPrivateKey so the Bouncy Castle backend can export its parameters for signing/decryption.
        // A PKCS#12 round-trip would leave a non-exportable CNG key on .NET 8 (CngKey.Export throws).
        var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certWithKey = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

#pragma warning disable SYSLIB0057 // byte[] ctor is obsolete on net9+ but not net8; suppress across TFMs
        using var publicOnly = new X509Certificate2(certWithKey.Export(X509ContentType.Cert));
#pragma warning restore SYSLIB0057
        return publicOnly.CopyWithPrivateKey(rsa);
    }
}
