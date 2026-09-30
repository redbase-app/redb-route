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
public sealed class As4RevocationTests : IAsyncDisposable
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
        serial[0] &= 0x7F;
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

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        await _loop.ConfigureAwait(false);
    }
}
