using System.Net;
using FluentAssertions;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;
using static redb.Route.Tests.As2.As2TestKit;

namespace redb.Route.Tests.As2;

/// <summary>
/// What a receipt proves (REVIEW-2026-09-28 R2, R3): a transfer is confirmed only by a positive MDN whose
/// Received-Content-MIC is the one we sent and whose signature the agreement's policy accepts. A missing MIC, an
/// unknown message, or an unsigned receipt where a signed one is agreed is not a confirmation.
/// </summary>
public class As2MdnVerdictTests
{
    private static (string ContentType, string? Cte, byte[] Body) Mdn(string originalId, As2Mic? mic, bool signed = true, string? failure = null) =>
        MdnBuilder.Build(originalId, "US", "THEM", mic, failure, null,
            signer: signed ? new As2CryptoEngine() : null, signerCert: signed ? Cert : null);

    /// <summary>A partner that answers every POST with the given MDN, built for the Message-ID it received.</summary>
    private static Task Partner(HttpListener listener, Func<string, (string ContentType, string? Cte, byte[] Body)> mdnFor) => Task.Run(async () =>
    {
        var ctx = await listener.GetContextAsync();
        var messageId = ctx.Request.Headers["Message-ID"]!;
        await ctx.Request.InputStream.CopyToAsync(Stream.Null);
        var (contentType, cte, body) = mdnFor(messageId);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = contentType;
        if (cte is not null) ctx.Response.Headers["Content-Transfer-Encoding"] = cte;
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    });

    private static async Task<(Exchange Exchange, Exception? Error)> SendAsync(
        Func<string, (string ContentType, string? Cte, byte[] Body)> mdnFor, Action<As2ConnectionFactory>? configure = null)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var partner = Partner(listener, mdnFor);

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(configure));
        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/as2").ConnectionFactory("them")).CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("PO*MDN~") { ContentType = "application/edi-x12" });
        Exception? error = null;
        try { await producer.Process(exchange); }
        catch (Exception e) { error = e; }
        await partner.WaitAsync(TimeSpan.FromSeconds(10));
        return (exchange, error);
    }

    [Fact]
    public async Task SyncMdn_WithoutReceivedContentMic_IsNotAMatch()
    {
        var (exchange, error) = await SendAsync(id => Mdn(id, mic: null));

        error.Should().BeNull();
        exchange.Out!.GetHeader<bool>(As2Headers.MdnMicMatch).Should().BeFalse("nobody compared a MIC");
        exchange.Out!.GetHeader<string>(As2Headers.MdnMicStatus).Should().Be("absent");
        exchange.Out!.GetHeader<bool>(As2Headers.MdnConfirmed).Should().BeFalse();
    }

    [Fact]
    public async Task SyncMdn_WithoutReceivedContentMic_FailsTheSend_WhenAValidMdnIsRequired()
    {
        var (_, error) = await SendAsync(id => Mdn(id, mic: null), f => f.RequireValidMdn = true);

        error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("absent");
    }

    [Fact]
    public async Task SyncMdn_WithAnotherMic_IsAMismatch()
    {
        var (exchange, _) = await SendAsync(id => Mdn(id, new As2Mic("AAAA", "sha-256")));

        exchange.Out!.GetHeader<string>(As2Headers.MdnMicStatus).Should().Be("mismatch");
        exchange.Out!.GetHeader<bool>(As2Headers.MdnConfirmed).Should().BeFalse();
    }

    [Fact]
    public void Mic_ComparesTheDigestBytes_NotItsSpelling()
    {
        var sent = new As2Mic("q1w2e3r4t5y6u7i8o9p0aZ==", "sha-256");

        sent.Matches(new As2Mic(" q1w2e3r4t5y6\r\n u7i8o9p0aZ== ", "SHA-256")).Should().BeTrue();
        sent.Matches(new As2Mic("q1w2e3r4t5y6u7i8o9p0aZ", "sha256")).Should().BeTrue("base64 without padding is the same digest");
        sent.Matches(new As2Mic("q1w2e3r4t5y6u7i8o9p0aA==", "sha-256")).Should().BeFalse();
        sent.Matches(new As2Mic("q1w2e3r4t5y6u7i8o9p0aZ==", "sha-1")).Should().BeFalse();
    }

    // ── Asynchronous MDN receiver ────────────────────────────────────────────

    private sealed record Delivered(string? MessageId, bool MicMatch, string? MicStatus, bool Confirmed, bool SignatureValid);

    private static async Task<(RouteContext Context, As2Component Component, List<Delivered> Delivered, int Port)> StartMdnReceiver(
        Action<As2ConnectionFactory>? configure = null)
    {
        var port = FreePort();
        var context = new RouteContext();
        var component = new As2Component();
        context.AddComponent(component);
        context.AddToRegistry("them", Sender(f =>
        {
            f.MdnMode = As2MdnMode.Async;
            f.AsyncMdnUrl = $"http://127.0.0.1:{port}/mdn";
            configure?.Invoke(f);
        }));
        var delivered = new List<Delivered>();
        context.AddRoutes(r =>
            r.From(As2Dsl.ReceiveMdn("/mdn").Host("127.0.0.1").Port(port).ConnectionFactory("them"))
                .Process(e =>
                {
                    lock (delivered)
                        delivered.Add(new Delivered(
                            e.In.GetHeader<string>(As2Headers.MessageId),
                            e.In.GetHeader<bool>(As2Headers.MdnMicMatch),
                            e.In.GetHeader<string>(As2Headers.MdnMicStatus),
                            e.In.GetHeader<bool>(As2Headers.MdnConfirmed),
                            e.In.GetHeader<bool>(As2Headers.SignatureValid)));
                }));
        await context.Start();
        return (context, component, delivered, port);
    }

    private static async Task<HttpResponseMessage> PostMdn(int port, (string ContentType, string? Cte, byte[] Body) mdn)
    {
        using var client = new HttpClient();
        var content = new ByteArrayContent(mdn.Body);
        content.Headers.TryAddWithoutValidation("Content-Type", mdn.ContentType);
        if (mdn.Cte is not null) content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", mdn.Cte);
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mdn") { Content = content };
        request.Headers.TryAddWithoutValidation("AS2-From", "US");
        request.Headers.TryAddWithoutValidation("AS2-To", "THEM");
        request.Headers.TryAddWithoutValidation("Message-ID", $"<{Guid.NewGuid():N}@kit>");
        return await client.SendAsync(request);
    }

    private static readonly As2Mic SentMic = new("c2VudC1taWMtZGlnZXN0LWJ5dGVzLTAxMjM0NTY3ODk=", "sha-256");

    [Fact]
    public async Task AsyncMdn_ForAPendingMessage_WithItsMic_IsConfirmed()
    {
        var (context, component, delivered, port) = await StartMdnReceiver();
        await using var _ = context;
        component.Correlation.Register("<sent-1@kit>", SentMic);

        (await PostMdn(port, Mdn("<sent-1@kit>", SentMic))).StatusCode.Should().Be(HttpStatusCode.OK);

        delivered.Should().ContainSingle().Which.Should().Be(new Delivered("<sent-1@kit>", true, "matched", true, true));
        component.Correlation.ExpectedMic("<sent-1@kit>").Should().BeNull("the verdict is in; nothing waits any more");
    }

    [Fact]
    public async Task AsyncMdn_ForAMessageNobodyWaitsFor_IsNotAMatch()
    {
        // After a restart, after the correlation TTL, or on another node: the MIC it should carry is not known.
        var (context, _, delivered, port) = await StartMdnReceiver();
        await using var _ = context;

        await PostMdn(port, Mdn("<orphan@kit>", SentMic));

        delivered.Should().ContainSingle().Which.Should().Be(new Delivered("<orphan@kit>", false, "unknown", false, true));
    }

    [Fact]
    public async Task AsyncMdn_WithoutOriginalMessageId_IsNotAMatch()
    {
        var (context, _, delivered, port) = await StartMdnReceiver();
        await using var _ = context;

        await PostMdn(port, Mdn("", SentMic));

        delivered.Should().ContainSingle().Which.MicStatus.Should().Be("unknown");
        delivered[0].MicMatch.Should().BeFalse();
        delivered[0].Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task AsyncMdn_WithoutMic_ForAPendingMessage_IsAbsent()
    {
        var (context, component, delivered, port) = await StartMdnReceiver();
        await using var _ = context;
        component.Correlation.Register("<sent-2@kit>", SentMic);

        await PostMdn(port, Mdn("<sent-2@kit>", mic: null));

        delivered.Should().ContainSingle().Which.Should().Be(new Delivered("<sent-2@kit>", false, "absent", false, true));
    }

    [Fact]
    public async Task AsyncMdn_Unsigned_WhereASignedOneIsAgreed_IsNotAConfirmation()
    {
        var (context, component, delivered, port) = await StartMdnReceiver();
        await using var _ = context;
        component.Correlation.Register("<sent-3@kit>", SentMic);

        await PostMdn(port, Mdn("<sent-3@kit>", SentMic, signed: false));

        delivered.Should().ContainSingle().Which.Should().Be(new Delivered("<sent-3@kit>", true, "matched", false, false));
        component.Correlation.ExpectedMic("<sent-3@kit>").Should().Be(SentMic,
            "an unsigned receipt may be anyone's: the partner's own one must still find the message waiting");
    }

    [Fact]
    public async Task AsyncMdn_Unsigned_WhereAValidOneIsRequired_IsRefused_AndTheMessageStillWaits()
    {
        var (context, component, delivered, port) = await StartMdnReceiver(f => f.RequireValidMdn = true);
        await using var _ = context;
        component.Correlation.Register("<sent-4@kit>", SentMic);

        var forged = await PostMdn(port, Mdn("<sent-4@kit>", SentMic, signed: false));

        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        delivered.Should().BeEmpty("a receipt the agreement does not accept is not delivered as one");

        // The partner's genuine receipt still confirms the transfer.
        (await PostMdn(port, Mdn("<sent-4@kit>", SentMic))).StatusCode.Should().Be(HttpStatusCode.OK);
        delivered.Should().ContainSingle().Which.Confirmed.Should().BeTrue();
    }

    [Fact]
    public async Task AsyncMdn_Negative_FromThePartner_IsDelivered_Unconfirmed_EvenWhenAValidOneIsRequired()
    {
        // A negative receipt is the partner's word that the transfer failed: the route must see it.
        var (context, component, delivered, port) = await StartMdnReceiver(f => f.RequireValidMdn = true);
        await using var _ = context;
        component.Correlation.Register("<sent-5@kit>", SentMic);

        (await PostMdn(port, Mdn("<sent-5@kit>", SentMic, failure: As2Disposition.DecryptionFailed))).StatusCode.Should().Be(HttpStatusCode.OK);

        delivered.Should().ContainSingle().Which.Confirmed.Should().BeFalse();
    }
}

/// <summary>
/// A partner may post the asynchronous MDN before it answers the message's own POST (OpenAS2 does, often): the wait
/// must exist before the message is sent, or the receipt of a message we did send reads as one nobody waits for.
/// </summary>
public class As2AsyncMdnOrderTests
{
    [Fact]
    public async Task AsyncMdn_PostedBeforeThePartnerAnswers_IsStillCorrelated()
    {
        var partnerPort = FreePort();
        var mdnPort = FreePort();
        using var partner = new HttpListener();
        partner.Prefixes.Add($"http://127.0.0.1:{partnerPort}/");
        partner.Start();

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => { f.MdnMode = As2MdnMode.Async; f.AsyncMdnUrl = $"http://127.0.0.1:{mdnPort}/mdn"; }));
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        context.AddRoutes(r => r.From(As2Dsl.ReceiveMdn("/mdn").Host("127.0.0.1").Port(mdnPort).ConnectionFactory("them"))
            .Process(e => statuses.Enqueue(e.In.GetHeader<string>(As2Headers.MdnMicStatus))));
        await context.Start();

        var serving = Task.Run(async () =>
        {
            var ctx = await partner.GetContextAsync();
            var id = ctx.Request.Headers["Message-ID"]!;
            await ctx.Request.InputStream.CopyToAsync(Stream.Null);
            // The receipt first (without a MIC, so a correlated one reads "absent" and an orphan "unknown"), then the 200.
            var (ct, cte, body) = MdnBuilder.Build(id, "US", "THEM", null, signer: new As2CryptoEngine(), signerCert: Cert);
            using var client = new HttpClient();
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", ct);
            if (cte is not null) content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", cte);
            (await client.PostAsync($"http://127.0.0.1:{mdnPort}/mdn", content)).EnsureSuccessStatusCode();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{partnerPort}/as2").ConnectionFactory("them")).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("PO*EARLY~") { ContentType = "application/edi-x12" }));
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

        statuses.Should().ContainSingle().Which.Should().Be("absent", "the message was sent and waited for: its MIC was compared, and is missing");
    }

    [Fact]
    public async Task FailedSend_LeavesNothingWaiting()
    {
        var mdnPort = FreePort();
        await using var context = new RouteContext();
        var component = new As2Component();
        context.AddComponent(component);
        context.AddToRegistry("them", Sender(f => { f.MdnMode = As2MdnMode.Async; f.AsyncMdnUrl = $"http://127.0.0.1:{mdnPort}/mdn"; }));
        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{FreePort()}/as2").ConnectionFactory("them")).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("PO*NOWHERE~") { ContentType = "application/edi-x12" });

        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<HttpRequestException>();
        component.Correlation.ExpectedMic(exchange.In.GetHeader<string>(As2Headers.MessageId)!).Should().BeNull(
            "a message that was not delivered is not waiting for a receipt");
    }
}
