using System.Net;
using System.Text;
using FluentAssertions;
using MimeKit;
using MimeKit.Cryptography;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;
using static redb.Route.Tests.As2.As2TestKit;

namespace redb.Route.Tests.As2;

/// <summary>
/// The MDN a receiver returns is the one the request asked for (REVIEW-2026-09-28 R12, RFC 4130 §7.3), credentials of
/// the transport hop do not travel with the document (R7), and a disposition is read by its fields (R11).
/// </summary>
public class As2ProtocolTests
{
    private static async Task<(RouteContext Context, System.Collections.Concurrent.ConcurrentQueue<IDictionary<string, object?>> Routed, int Port)> StartReceiver()
    {
        var port = FreePort();
        var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver());
        var routed = new System.Collections.Concurrent.ConcurrentQueue<IDictionary<string, object?>>();
        context.AddRoutes(r => r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv"))
            .Process(e => routed.Enqueue(new Dictionary<string, object?>(e.In.Headers, StringComparer.OrdinalIgnoreCase))));
        await context.Start();
        return (context, routed, port);
    }

    private static MimeEntity Entity(HttpResponseMessage response)
    {
        var sb = new StringBuilder();
        sb.Append("Content-Type: ").Append(response.Content.Headers.ContentType).Append("\r\n");
        if (response.Content.Headers.TryGetValues("Content-Transfer-Encoding", out var cte))
            sb.Append("Content-Transfer-Encoding: ").Append(string.Join(",", cte)).Append("\r\n");
        sb.Append("\r\n");
        var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.Write(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        stream.Position = 0;
        return MimeEntity.Load(stream);
    }

    // ── R12 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Message_ThatAsksForNoMdn_IsAnsweredWithoutOne()
    {
        var (context, routed, port) = await StartReceiver();
        await using var _ = context;

        var response = await PostAsync(port, "/in", BuildMessage("PO*NOMDN~", Cert, Cert),
            new Dictionary<string, string?> { ["Disposition-Notification-To"] = null, ["Disposition-Notification-Options"] = null });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty("RFC 4130 §7.3: no Disposition-Notification-To, no MDN");
        routed.Should().ContainSingle();
    }

    [Fact]
    public async Task Mdn_IsSigned_OnlyWhenTheRequestAsksForASignedReceipt()
    {
        var (context, _, port) = await StartReceiver();
        await using var _ = context;

        var unsigned = await PostAsync(port, "/in", BuildMessage("PO*PLAINMDN~", Cert, Cert),
            new Dictionary<string, string?> { ["Disposition-Notification-Options"] = null });
        var signed = await PostAsync(port, "/in", BuildMessage("PO*SIGNEDMDN~", Cert, Cert));

        unsigned.Content.Headers.ContentType!.MediaType.Should().Be("multipart/report");
        signed.Content.Headers.ContentType!.MediaType.Should().Be("multipart/signed");
    }

    [Fact]
    public async Task Mdn_UsesTheRequestedMicAlgorithm_ForTheMicAndItsSignature()
    {
        var (context, _, port) = await StartReceiver();
        await using var _ = context;

        var response = await PostAsync(port, "/in", BuildMessage("PO*384~", Cert, Cert, "sha-384"),
            new Dictionary<string, string?>
            {
                ["Disposition-Notification-Options"] = "signed-receipt-protocol=required, pkcs7-signature; signed-receipt-micalg=required, sha-384, sha-256",
            });

        var signed = Entity(response).Should().BeOfType<MultipartSigned>().Subject;
        signed.ContentType.Parameters["micalg"].Should().Be("sha-384");
        var mdn = MdnParser.Parse(response.Content.Headers.ContentType!.ToString(),
            response.Content.Headers.TryGetValues("Content-Transfer-Encoding", out var v) ? string.Join(",", v) : null,
            await response.Content.ReadAsByteArrayAsync(), new As2CryptoEngine(), Cert);
        mdn.ReceivedMic!.Value.Algorithm.Should().Be("sha-384");
        mdn.SignatureValid.Should().BeTrue();
    }

    [Fact]
    public async Task Producer_AsksForASignedReceipt_AsRequired_WhenTheAgreementWantsOne()
    {
        using var trap = new As2Trap(FreePort());
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => f.MdnMode = As2MdnMode.None));
        context.AddToRegistry("them-mdn", Sender(f => f.SignAlg = "sha-384"));
        var none = context.GetEndpoint(As2Dsl.Send(trap.Url("/as2")).ConnectionFactory("them")).CreateProducer();
        var signed = context.GetEndpoint(As2Dsl.Send(trap.Url("/as2b")).ConnectionFactory("them-mdn")).CreateProducer();
        await none.Start();
        await signed.Start();

        await none.Process(new Exchange(new Message("PO*1~")));
        try { await signed.Process(new Exchange(new Message("PO*2~"))); }
        catch (Exception) { /* the trap answers 200 with no MDN; only the request matters here */ }

        var requests = trap.Requests.ToArray();
        requests.Single(r => r.Path == "/as2").Headers.Should().NotContainKey("Disposition-Notification-To");
        requests.Single(r => r.Path == "/as2b").Headers["Disposition-Notification-Options"].Should()
            .Be("signed-receipt-protocol=required, pkcs7-signature; signed-receipt-micalg=required, sha-384");
    }

    // ── R7 ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Receiver_DoesNotHandTheHopsCredentialsToTheRoute()
    {
        var (context, routed, port) = await StartReceiver();
        await using var _ = context;

        await PostAsync(port, "/in", BuildMessage("PO*CREDS~", Cert, Cert), new Dictionary<string, string?>
        {
            ["Authorization"] = "Basic ZWRpOnMzY3IzdA==",
            ["Proxy-Authorization"] = "Basic cHJveHk6cHc=",
            ["Cookie"] = "session=abc",
            ["X-Business"] = "kept",
        });

        var headers = routed.Should().ContainSingle().Subject;
        headers.Should().NotContainKeys("Authorization", "Proxy-Authorization", "Cookie");
        headers["X-Business"].Should().Be("kept");
    }

    [Fact]
    public async Task Producer_DoesNotBridgeCredentials()
    {
        using var trap = new As2Trap(FreePort());
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => f.MdnMode = As2MdnMode.None));
        var producer = context.GetEndpoint(As2Dsl.Send(trap.Url("/as2")).ConnectionFactory("them")).CreateProducer();
        await producer.Start();

        var message = new Message("PO*BRIDGE~");
        message.Headers["Authorization"] = "Bearer internal-token";
        message.Headers["Cookie"] = "session=abc";
        message.Headers["Set-Cookie"] = "a=b";
        message.Headers["X-Business"] = "kept";
        await producer.Process(new Exchange(message));

        (await trap.HitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        trap.Requests.TryPeek(out var hit);
        hit.Headers.Should().NotContainKeys("Authorization", "Cookie", "Set-Cookie");
        hit.Headers["X-Business"].Should().Be("kept");
    }

    // ── R11: the disposition by its fields ───────────────────────────────────

    [Theory]
    [InlineData("automatic-action/MDN-sent-automatically; processed", true)]
    [InlineData("automatic-action/MDN-sent-automatically; processed/warning: duplicate-document", true)]
    [InlineData("automatic-action/MDN-sent-automatically; processed/warning: error-free after retry", true)]
    [InlineData("automatic-action/MDN-sent-automatically; processed/error: decryption-failed", false)]
    [InlineData("automatic-action/MDN-sent-automatically; processed/Error: authentication-failed", false)]
    [InlineData("automatic-action/MDN-sent-automatically; failed/Failure: unsupported format", false)]
    [InlineData("automatic-action/MDN-sent-automatically; processed/failure: sender-equals-receiver", false)]
    [InlineData("automatic-action/MDN-sent-automatically; denied", false)]
    [InlineData("processed", false)]
    public void Disposition_IsPositive_ByItsTypeAndModifier(string disposition, bool positive)
    {
        MdnParser.IsPositiveDisposition(disposition).Should().Be(positive);
    }
}

/// <summary>
/// RFC 3798 §3.1: <c>message/disposition-notification</c> is 7bit, "MUST be used". A long Original-Message-ID (OpenAS2's
/// are ~90 characters) must not push the machine part into quoted-printable: OpenAS2 reads the field undecoded, cuts the
/// id at the soft line break, and never finds the message an asynchronous MDN reports on.
/// </summary>
public class As2MdnEncodingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MachinePart_Stays7bit_AndCarriesALongMessageIdWhole(bool signed)
    {
        var id = "<20260928195636+0000-992-openas2_redb-async_async-40c2a6adcca248639558cf38a4c0e4c2abcdef.edi>";
        var (_, _, body) = MdnBuilder.Build(id, "redb-async", "openas2", new As2Mic("u2OyNRM8PK2R8YJqYxwovz/9lrGpcFKb+vghhpoABMo=", "sha-256"),
            signer: signed ? new As2CryptoEngine() : null, signerCert: signed ? As2TestKit.Cert : null);
        var text = Encoding.ASCII.GetString(body);

        text.Should().NotContain("quoted-printable");
        text.Should().Contain("Original-Message-ID: " + id + "\r\n");
        text.Should().Contain("Received-Content-MIC: u2OyNRM8PK2R8YJqYxwovz/9lrGpcFKb+vghhpoABMo=, sha-256\r\n");
    }
}

/// <summary>
/// RFC 4130 §7.3.1: the receiver's MIC is over the signed part as it was signed (its MIME headers and content, as
/// received). OpenAS2 signs a <c>binary</c> part; re-preparing it for 7bit before hashing turned it into base64 and gave
/// a MIC OpenAS2 rejects ("MIC not matched", seen on its asynchronous MDN check, which the synchronous path skips).
/// </summary>
public class As2ReceivedMicTests
{
    [Fact]
    public void Mic_OfAReceivedBinaryPart_IsOverItsBytesAsReceived()
    {
        const string part = "Content-Type: application/edi-x12\r\nContent-Transfer-Encoding: binary\r\n\r\nISA*00*BINARY~\r\nLINEé~";
        var raw = "Content-Type: multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=sha-256; boundary=\"b\"\r\n\r\n" +
                  "--b\r\n" + part + "\r\n--b\r\nContent-Type: application/pkcs7-signature\r\nContent-Transfer-Encoding: base64\r\n\r\nAAAA\r\n--b--\r\n";
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes(raw));
        var signed = (MultipartSigned)MimeEntity.Load(stream, persistent: true);

        var mic = new As2CryptoEngine().ComputeReceivedMic(signed[0], "sha-256");

        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.Latin1.GetBytes(part)));
        mic.Should().Be(new As2Mic(expected, "sha-256"));
    }
}
