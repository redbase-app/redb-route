using System.Net;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;
using static redb.Route.Tests.As2.As2TestKit;

namespace redb.Route.Tests.As2;

/// <summary>
/// The agreement is checked when an endpoint starts, not on the first message (REVIEW-2026-09-28 R6, R16, R17), and a
/// certificate is used only while it is valid (R10).
/// </summary>
public class As2AgreementTests
{
    private static readonly X509Certificate2 Expired = MakeCert("as2-expired",
        DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1));

    private static readonly X509Certificate2 NotYetValid = MakeCert("as2-future",
        DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddYears(2));

#pragma warning disable SYSLIB0057
    private static readonly X509Certificate2 PublicOnly = new(Cert.Export(X509ContentType.Cert));
#pragma warning restore SYSLIB0057

    // ── R6: As2ConnectionFactory.Validate ────────────────────────────────────

    public static TheoryData<string, Action<As2ConnectionFactory>, string> Broken => new()
    {
        { "no our id", f => f.As2From = "", "As2From" },
        { "no partner id", f => f.As2To = " ", "As2To" },
        { "unknown digest", f => f.SignAlg = "sha2-256", "SignAlg" },
        { "unknown cipher", f => f.EncryptAlg = "aes-128-gcm", "EncryptAlg" },
        { "no own certificate", f => f.OurCertificate = null, "OurCertificate" },
        { "own certificate without key", f => f.OurCertificate = PublicOnly, "private key" },
        { "no partner certificate", f => f.PartnerCertificate = null, "PartnerCertificate" },
        { "relative receipt url", f => { f.MdnMode = As2MdnMode.Async; f.AsyncMdnUrl = "/mdn"; }, "AsyncMdnUrl" },
        { "relative partner url", f => f.PartnerUrl = "partner/as2", "PartnerUrl" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void Validate_RefusesAnIncompleteAgreement(string _, Action<As2ConnectionFactory> breakIt, string named)
    {
        var factory = Sender(breakIt);

        var act = () => factory.Validate("walmart");

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("walmart").And.Contain(named);
    }

    [Fact]
    public void Validate_AcceptsAnAgreementWithoutCertificates_WhenNothingIsSignedOrEncrypted()
    {
        var factory = Sender(f =>
        {
            f.OurCertificate = null; f.PartnerCertificate = null;
            f.Sign = false; f.Encrypt = false; f.SignedMdn = false;
        });

        factory.Invoking(f => f.Validate("plain")).Should().NotThrow();
    }

    [Fact]
    public async Task Receiver_WithAnInvalidAgreement_DoesNotStart()
    {
        var component = new As2Component();
        await using var context = new RouteContext();
        context.AddComponent(component);
        context.AddToRegistry("recv", Receiver(f => f.SignAlg = "sha2-256"));
        var consumer = context.GetEndpoint(As2Dsl.Receive("/in").Host("127.0.0.1").Port(FreePort()).ConnectionFactory("recv"))
            .CreateConsumer(new redb.Route.Processors.DelegateProcessor(_ => { }));

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("SignAlg");
    }

    [Fact]
    public async Task MdnReceiver_WithAnInvalidAgreement_DoesNotStart()
    {
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => f.PartnerCertificate = null));
        var consumer = context.GetEndpoint(As2Dsl.ReceiveMdn("/mdn").Host("127.0.0.1").Port(FreePort()).ConnectionFactory("them"))
            .CreateConsumer(new redb.Route.Processors.DelegateProcessor(_ => { }));

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("PartnerCertificate");
    }

    [Fact]
    public async Task Producer_WithAnInvalidAgreement_DoesNotStart()
    {
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => f.OurCertificate = null));
        var producer = context.GetEndpoint(As2Dsl.Send("http://127.0.0.1:1/as2").ConnectionFactory("them")).CreateProducer();

        var act = () => producer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("OurCertificate");
    }

    // ── R16: one source of the agreement ─────────────────────────────────────

    [Theory]
    [InlineData("sign=false")]
    [InlineData("signAlg=sha-384")]
    [InlineData("mdnMode=None")]
    [InlineData("as2From=OTHER")]
    [InlineData("asyncMdnAllowedHosts=example.com")]
    public void Endpoint_NamingAFactory_RefusesInlineAgreementOptions(string inline)
    {
        var component = new As2Component();
        var act = () => component.CreateEndpoint(EndpointUriParser.Parse($"as2:/in?port=4080&connectionFactory=walmart&{inline}"));

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain(inline.Split('=')[0]).And.Contain("connection factory");
    }

    [Fact]
    public void CertPassword_IsNotAnOption_AndSaysWhereCertificatesGo()
    {
        var component = new As2Component();
        var act = () => component.CreateEndpoint(EndpointUriParser.Parse("as2:/in?port=4080&certPassword=secret"));

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("As2ConnectionFactory").And.NotContain("secret");
    }

    [Fact]
    public async Task Producer_WhoseFactoryNamesAnotherPartnerUrl_DoesNotStart()
    {
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => f.PartnerUrl = "https://partner.example/as2"));
        var producer = context.GetEndpoint(As2Dsl.Send("http://127.0.0.1:1/as2").ConnectionFactory("them")).CreateProducer();

        var act = () => producer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("PartnerUrl");
    }

    // ── R17: legacy algorithms only on request ───────────────────────────────

    [Theory]
    [InlineData("sha-1", "aes-128-cbc")]
    [InlineData("sha1", "aes-128-cbc")]
    [InlineData("sha-256", "3des")]
    [InlineData("sha-256", "des-ede3-cbc")]
    public void LegacyAlgorithms_AreRefused_UnlessAllowed(string signAlg, string encryptAlg)
    {
        var refused = Sender(f => { f.SignAlg = signAlg; f.EncryptAlg = encryptAlg; });
        refused.Invoking(f => f.Validate("old")).Should().Throw<InvalidOperationException>().WithMessage("*AllowLegacyAlgorithms*");

        var allowed = Sender(f => { f.SignAlg = signAlg; f.EncryptAlg = encryptAlg; f.AllowLegacyAlgorithms = true; });
        allowed.Invoking(f => f.Validate("old")).Should().NotThrow();
    }

    [Fact]
    public void LegacyAlgorithms_Inline_AreRefused_UnlessAllowed()
    {
        var component = new As2Component();
        var refused = () => component.CreateEndpoint(EndpointUriParser.Parse("as2:/in?port=4080&signAlg=sha-1"));
        refused.Should().Throw<ArgumentException>().WithMessage("*allowLegacyAlgorithms*");

        var allowed = () => component.CreateEndpoint(EndpointUriParser.Parse("as2:/in?port=4080&signAlg=sha-1&allowLegacyAlgorithms=true"));
        allowed.Should().NotThrow();
    }

    // ── R10: certificate validity ────────────────────────────────────────────

    [Fact]
    public async Task Message_SignedWithAnExpiredPartnerCertificate_IsRefused()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver(f => f.PartnerCertificate = Expired));
        var delivered = 0;
        context.AddRoutes(r => r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv"))
            .Process(_ => Interlocked.Increment(ref delivered)));
        await context.Start();

        var response = await PostAsync(port, "/in", BuildMessage("PO*EXPIRED~", Expired, Cert));

        delivered.Should().Be(0);
        var cte = response.Content.Headers.TryGetValues("Content-Transfer-Encoding", out var v) ? string.Join(",", v) : null;
        var mdn = MdnParser.Parse(response.Content.Headers.ContentType?.ToString(), cte,
            await response.Content.ReadAsByteArrayAsync(), new As2CryptoEngine(), Cert);
        mdn.Disposition.Should().EndWith("processed/error: authentication-failed");
    }

    [Theory]
    [InlineData("partner")]
    [InlineData("ours")]
    [InlineData("future")]
    public async Task Send_WithACertificateOutsideItsValidity_FailsBeforePosting(string which)
    {
        using var trap = new As2Trap(FreePort());
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f =>
        {
            if (which == "partner") f.PartnerCertificate = Expired;
            else if (which == "ours") f.OurCertificate = Expired;
            else f.PartnerCertificate = NotYetValid;
        }));
        var producer = context.GetEndpoint(As2Dsl.Send(trap.Url("/as2")).ConnectionFactory("them")).CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message("PO*VALIDITY~") { ContentType = "application/edi-x12" }));

        (await act.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>())
            .Which.Message.Should().MatchRegex("expired|not yet valid");
        (await trap.HitAsync(TimeSpan.FromMilliseconds(300))).Should().BeFalse();
    }

    [Fact]
    public async Task SyncMdn_SignedWithAnExpiredPartnerCertificate_IsNotAValidSignature()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var partner = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var id = ctx.Request.Headers["Message-ID"]!;
            await ctx.Request.InputStream.CopyToAsync(Stream.Null);
            var (ct, cte, body) = MdnBuilder.Build(id, "US", "THEM", null, null, null, new As2CryptoEngine(), Expired);
            ctx.Response.ContentType = ct;
            if (cte is not null) ctx.Response.Headers["Content-Transfer-Encoding"] = cte;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        // The MDN signer's certificate is pinned, but expired: sending is refused with it, so it signs only the MDN here.
        context.AddToRegistry("them", Sender(f => { f.Encrypt = false; f.Sign = false; f.PartnerCertificate = Expired; }));
        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/as2").ConnectionFactory("them")).CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("PO*MDN~") { ContentType = "application/edi-x12" });
        await producer.Process(exchange);
        await partner.WaitAsync(TimeSpan.FromSeconds(10));

        exchange.Out!.GetHeader<bool>(As2Headers.SignatureValid).Should().BeFalse();
    }
}
