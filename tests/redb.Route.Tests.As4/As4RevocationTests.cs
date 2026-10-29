using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using redb.Route.As4.Security;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф7: revocation of pinned certificates, against a real CRL a local CA publishes over HTTP (with its certificate at the
/// AIA caIssuers address, so the chain to the CRL signer can be built). The policy is phase4's and Domibus':
/// hard-fail when the status cannot be determined unless soft-fail is chosen; a certificate that names no CRL
/// distribution point and no AIA passes, as in Domibus. Off by default, as in Camel/CXF, Holodeck and phase4.
/// </summary>
public sealed class As4RevocationTests : IAsyncLifetime
{
    private readonly HttpListener _listener = new();
    private readonly int _port = global::redb.Route.Tests.Shared.TestPorts.Next();
    private readonly string _prefix = "/" + Guid.NewGuid().ToString("N");   // fresh URLs: the OS caches CRLs by URL
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Task _loop;

    public As4RevocationTests()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    [Fact]
    public void NotRevoked_Passes()
    {
        var (_, leaf) = Pki(revoke: false);
        CertificateValidity.Problem(leaf, DateTimeOffset.UtcNow, "The partner certificate", Online()).Should().BeNull();
    }

    [Fact]
    public void Revoked_IsRefused()
    {
        var (_, leaf) = Pki(revoke: true);
        CertificateValidity.Problem(leaf, DateTimeOffset.UtcNow, "The partner certificate", Online())
            .Should().Contain("revoked");
    }

    [Fact]
    public void Revoked_ButNotChecked_Passes()
    {
        var (_, leaf) = Pki(revoke: true);
        CertificateValidity.Problem(leaf, DateTimeOffset.UtcNow, "The partner certificate", RevocationPolicy.None).Should().BeNull();
    }

    [Fact]
    public void UnreachableCrl_IsRefused_UnlessSoftFail()
    {
        var (_, leaf) = Pki(revoke: false, publish: false);

        CertificateValidity.Problem(leaf, DateTimeOffset.UtcNow, "The partner certificate", Online())
            .Should().Contain("could not be determined");
        CertificateValidity.Problem(leaf, DateTimeOffset.UtcNow, "The partner certificate", Online() with { SoftFail = true })
            .Should().BeNull();
    }

    [Fact]
    public void CertificateWithoutRevocationPointers_Passes()
    {
        var selfSigned = As4TestMessages.KeyPair("CN=no-crl.as4.test");
        CertificateValidity.Problem(selfSigned, DateTimeOffset.UtcNow, "The partner certificate", Online()).Should().BeNull();
    }

    [Fact]
    public void MessageSignedWithARevokedCertificate_FailsVerification()
    {
        var (_, leaf) = Pki(revoke: true);
        var message = redb.Route.As4.Mime.SwaMessage.Create(As4TestMessages.UserMessage("m-1@redb.test", "p1@redb.test"));
        message.AddPart("p1@redb.test", "application/gzip", As4TestMessages.Gzip("<a/>"u8.ToArray()));
        As4SecurityEngine.Sign(message, leaf, redb.Route.As4.As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);

        var act = () => As4SecurityEngine.Verify(message, [As4TestMessages.PublicOnly(leaf)], TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, Online());

        act.Should().Throw<CryptographicException>().WithMessage("*revoked*");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static RevocationPolicy Online() => new(X509RevocationMode.Online, SoftFail: false);

    /// <summary>A CA and a leaf it issued whose CDP and AIA point at this test's listener; the CRL lists the leaf when revoked.</summary>
    private (X509Certificate2 Ca, X509Certificate2 Leaf) Pki(bool revoke, bool publish = true)
    {
        var crlUrl = $"http://127.0.0.1:{_port}{_prefix}/ca.crl";
        var caUrl = $"http://127.0.0.1:{_port}{_prefix}/ca.cer";

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Test CA " + _prefix, caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(1));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=partner.as4.test", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafRequest.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crlUrl]));
        leafRequest.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [caUrl]));
        var serial = RandomNumberGenerator.GetBytes(16);
        // Positive and without a redundant leading byte (DER INTEGER): a first byte of zero followed by one below 0x80
        // is refused by CertificateRevocationListBuilder.AddEntry, which a plain "& 0x7F" produced about once in 512.
        serial[0] = (byte)((serial[0] & 0x7F) | 0x40);
        using var issued = leafRequest.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), serial);
        var leaf = issued.CopyWithPrivateKey(leafKey);

        if (publish)
        {
            var crl = new CertificateRevocationListBuilder();
            if (revoke)
                crl.AddEntry(serial, DateTimeOffset.UtcNow.AddHours(-1), X509RevocationReason.KeyCompromise);
            _files[$"{_prefix}/ca.crl"] = crl.Build(ca, BigInteger.One, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1, DateTimeOffset.UtcNow.AddMinutes(-5));
            _files[$"{_prefix}/ca.cer"] = ca.Export(X509ContentType.Cert);
        }
        return (ca, leaf);
    }

    // ── A certification path (eDelivery AS4 1.16 §4.3.3) ─────────────────────

    [Fact]
    public void Path_NotRevoked_Passes()
    {
        var pki = PathPki();
        PkiPath.EnsureTrusted([pki.Root, pki.Intermediate, pki.Signer], new PkiPathTrust([pki.Root], []), DateTimeOffset.UtcNow, Online());
    }

    [Fact]
    public void Path_WithARevokedSigner_IsRefused()
    {
        var pki = PathPki(revokeSigner: true);

        var act = () => PkiPath.EnsureTrusted([pki.Root, pki.Intermediate, pki.Signer], new PkiPathTrust([pki.Root], []), DateTimeOffset.UtcNow, Online());

        act.Should().Throw<CryptographicException>().WithMessage("The signing certificate CN=signer*has been revoked.");
    }

    [Fact]
    public void Path_WithARevokedIntermediateAuthority_IsRefused()
    {
        var pki = PathPki(revokeIntermediate: true);

        var act = () => PkiPath.EnsureTrusted([pki.Root, pki.Intermediate, pki.Signer], new PkiPathTrust([pki.Root], []), DateTimeOffset.UtcNow, Online());

        act.Should().Throw<CryptographicException>().WithMessage("The certification authority CN=Issuing CA*has been revoked.");
    }

    [Fact]
    public void Path_WithARevokedSigner_UnderAnIntermediateAnchor_IsRefused()
    {
        // .NET checks no revocation under an intermediate trust anchor (a partial chain, on Windows and on Linux): the
        // root the token carries above the anchor completes the chain for the check.
        var pki = PathPki(revokeSigner: true);

        var act = () => PkiPath.EnsureTrusted([pki.Root, pki.Intermediate, pki.Signer], new PkiPathTrust([pki.Intermediate], []), DateTimeOffset.UtcNow, Online());

        act.Should().Throw<CryptographicException>().WithMessage("*has been revoked.");
    }

    [Fact]
    public void Path_WithARevokedSigner_UnderAnIntermediateAnchor_TokenWithoutTheRoot_IsRefused()
    {
        // The token stops at the anchor: the root is found from the anchor's authority information access.
        var pki = PathPki(revokeSigner: true);

        var act = () => PkiPath.EnsureTrusted([pki.Intermediate, pki.Signer], new PkiPathTrust([pki.Intermediate], []), DateTimeOffset.UtcNow, Online());

        act.Should().Throw<CryptographicException>().WithMessage("*has been revoked.");
    }

    [Fact]
    public void Path_UnderAnIntermediateAnchor_WithNoRootToBeFound_IsUndetermined_UnlessSoftFail()
    {
        var pki = PathPki(intermediateNamesItsIssuer: false);
        var trust = new PkiPathTrust([pki.Intermediate], []);

        var act = () => PkiPath.EnsureTrusted([pki.Intermediate, pki.Signer], trust, DateTimeOffset.UtcNow, Online());

        act.Should().Throw<CryptographicException>().WithMessage("*could not be determined (no self-signed root above the trust anchor*");
        PkiPath.EnsureTrusted([pki.Intermediate, pki.Signer], trust, DateTimeOffset.UtcNow, Online() with { SoftFail = true });
    }

    [Fact]
    public void Path_WithAnUnreachableCrl_IsUndetermined_UnlessSoftFail()
    {
        var pki = PathPki(publishSignerCrl: false);
        var trust = new PkiPathTrust([pki.Root], []);

        var act = () => PkiPath.EnsureTrusted([pki.Root, pki.Intermediate, pki.Signer], trust, DateTimeOffset.UtcNow, Online());

        act.Should().Throw<CryptographicException>().WithMessage("The signing certificate*could not be determined*");
        PkiPath.EnsureTrusted([pki.Root, pki.Intermediate, pki.Signer], trust, DateTimeOffset.UtcNow, Online() with { SoftFail = true });
    }

    [Fact]
    public void Path_WithARevokedSigner_PassesWhenRevocationIsNotChecked()
    {
        var pki = PathPki(revokeSigner: true);
        PkiPath.EnsureTrusted([pki.Root, pki.Intermediate, pki.Signer], new PkiPathTrust([pki.Root], []), DateTimeOffset.UtcNow, RevocationPolicy.None);
    }

    [Fact]
    public void MessageSignedWithAPathToARevokedSigner_FailsVerification()
    {
        var pki = PathPki(revokeSigner: true);
        var message = redb.Route.As4.Mime.SwaMessage.Create(As4TestMessages.UserMessage("m-1@redb.test", "p1@redb.test"));
        message.AddPart("p1@redb.test", "application/gzip", As4TestMessages.Gzip("<a/>"u8.ToArray()));
        As4SecurityEngine.Sign(message, pki.Signer, redb.Route.As4.As4KeyReference.PkiPath, null, DateTimeOffset.UtcNow,
            [pki.Root, pki.Intermediate, pki.Signer]);

        var act = () => As4SecurityEngine.Verify(message, [], TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, Online(),
            pkiPath: new PkiPathTrust([pki.Root], []));

        act.Should().Throw<CryptographicException>().WithMessage("*revoked*");
    }

    /// <summary>
    /// root -> issuing CA -> signer, published by this test's listener: each CRL at its issuer's distribution point, each
    /// issuer's certificate at the caIssuers address of the certificates it issued.
    /// </summary>
    private (X509Certificate2 Root, X509Certificate2 Intermediate, X509Certificate2 Signer) PathPki(bool revokeSigner = false,
        bool revokeIntermediate = false, bool publishSignerCrl = true, bool intermediateNamesItsIssuer = true)
    {
        string Url(string file) => $"http://127.0.0.1:{_port}{_prefix}/{file}";

        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Path Root CA " + _prefix, rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(1));

        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest("CN=Issuing CA " + _prefix, intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
        if (intermediateNamesItsIssuer)
        {
            intermediateRequest.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([Url("root.crl")]));
            intermediateRequest.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [Url("root.cer")]));
        }
        var intermediateSerial = Serial();
        using var intermediateIssued = intermediateRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), intermediateSerial);
        var intermediate = intermediateIssued.CopyWithPrivateKey(intermediateKey);

        using var signerKey = RSA.Create(2048);
        var signerRequest = new CertificateRequest("CN=signer.as4.test", signerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        signerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        signerRequest.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([Url("issuing.crl")]));
        signerRequest.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [Url("issuing.cer")]));
        var signerSerial = Serial();
        using var signerIssued = signerRequest.Create(intermediate, DateTimeOffset.UtcNow.AddHours(-12), DateTimeOffset.UtcNow.AddMonths(3), signerSerial);
        var signer = signerIssued.CopyWithPrivateKey(signerKey);

        _files[$"{_prefix}/root.cer"] = root.Export(X509ContentType.Cert);
        _files[$"{_prefix}/issuing.cer"] = intermediate.Export(X509ContentType.Cert);
        _files[$"{_prefix}/root.crl"] = Crl(root, revokeIntermediate ? intermediateSerial : null);
        if (publishSignerCrl)
            _files[$"{_prefix}/issuing.crl"] = Crl(intermediate, revokeSigner ? signerSerial : null);

        return (As4TestMessages.PublicOnly(root), As4TestMessages.PublicOnly(intermediate), signer);

        static byte[] Serial()
        {
            var serial = RandomNumberGenerator.GetBytes(16);
            // Positive and without a redundant leading byte (DER INTEGER): a first byte of zero followed by one below 0x80
            // is refused by CertificateRevocationListBuilder.AddEntry, which a plain "& 0x7F" produced about once in 512.
            serial[0] = (byte)((serial[0] & 0x7F) | 0x40);
            return serial;
        }

        static byte[] Crl(X509Certificate2 issuer, byte[]? revoked)
        {
            var crl = new CertificateRevocationListBuilder();
            if (revoked is not null)
                crl.AddEntry(revoked, DateTimeOffset.UtcNow.AddHours(-1), X509RevocationReason.KeyCompromise);
            return crl.Build(issuer, BigInteger.One, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1, DateTimeOffset.UtcNow.AddMinutes(-5));
        }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext http;
            try { http = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (ArgumentException) when (!_listener.IsListening) { return; }

            if (_files.TryGetValue(http.Request.Url!.AbsolutePath, out var content))
            {
                http.Response.StatusCode = 200;
                http.Response.ContentType = http.Request.Url.AbsolutePath.EndsWith(".crl") ? "application/pkix-crl" : "application/pkix-cert";
                await http.Response.OutputStream.WriteAsync(content);
            }
            else
            {
                http.Response.StatusCode = 404;
            }
            http.Response.Close();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        await _loop.ConfigureAwait(false);
    }
}
