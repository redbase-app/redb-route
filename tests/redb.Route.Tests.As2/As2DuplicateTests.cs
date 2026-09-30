using System.Net;
using FluentAssertions;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using As2Dsl = redb.Route.As2.Fluent.As2;
using static redb.Route.Tests.As2.As2TestKit;

namespace redb.Route.Tests.As2;

/// <summary>
/// A partner that did not get our answer sends the document again with the same Message-ID (REVIEW-2026-09-28 R9).
/// With an idempotent repository named, the resend is answered and not delivered twice, as the AS4 receiver does.
/// </summary>
public class As2DuplicateTests
{
    private static async Task<(RouteContext Context, List<string> Routed, int Port)> StartReceiver(
        string? repository, Func<int, bool>? failOnAttempt = null)
    {
        var port = FreePort();
        var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver());
        context.AddIdempotentRepository("dedup", new InMemoryIdempotentRepository());
        var routed = new List<string>();
        var attempts = 0;
        var receive = As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv");
        if (repository is not null) receive = receive.IdempotentRepository(repository);
        context.AddRoutes(r => r.From(receive).Process(e =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (failOnAttempt?.Invoke(attempt) == true) throw new InvalidOperationException("the route is down");
            lock (routed) routed.Add(e.In.GetHeader<string>(As2Headers.MessageId)!);
        }));
        await context.Start();
        return (context, routed, port);
    }

    private static MdnParser.MdnResult Mdn(HttpResponseMessage response) => MdnParser.Parse(
        response.Content.Headers.ContentType?.ToString(),
        response.Content.Headers.TryGetValues("Content-Transfer-Encoding", out var v) ? string.Join(",", v) : null,
        response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult(), new As2CryptoEngine(), Cert);

    [Fact]
    public async Task Resend_OfADeliveredMessage_IsAnswered_AndNotDeliveredAgain()
    {
        var (context, routed, port) = await StartReceiver("dedup");
        await using var _ = context;
        var message = BuildMessage("PO*ONCE~", Cert, Cert);
        var id = new Dictionary<string, string?> { ["Message-ID"] = "<once@kit>" };

        var first = Mdn(await PostAsync(port, "/in", message, id));
        var again = Mdn(await PostAsync(port, "/in", message, id));

        routed.Should().ContainSingle().Which.Should().Be("<once@kit>");
        first.Disposition.Should().EndWith("; processed");
        again.IsPositive.Should().BeTrue("the document was processed, once");
        again.Disposition.Should().EndWith("processed/warning: duplicate-document");
        again.ReceivedMic.Should().Be(first.ReceivedMic);
    }

    [Fact]
    public async Task Resend_AfterTheRouteFailed_IsDelivered()
    {
        var (context, routed, port) = await StartReceiver("dedup", attempt => attempt == 1);
        await using var _ = context;
        var message = BuildMessage("PO*RETRY~", Cert, Cert);
        var id = new Dictionary<string, string?> { ["Message-ID"] = "<retry@kit>" };

        Mdn(await PostAsync(port, "/in", message, id)).IsPositive.Should().BeFalse();
        Mdn(await PostAsync(port, "/in", message, id)).IsPositive.Should().BeTrue();

        routed.Should().ContainSingle("the first attempt did not process the document, so its resend is not a duplicate");
    }

    [Fact]
    public async Task ForgedCopy_DoesNotBurnTheMessageId()
    {
        var (context, routed, port) = await StartReceiver("dedup");
        await using var _ = context;
        var id = new Dictionary<string, string?> { ["Message-ID"] = "<real@kit>" };

        // Claimed only after authentication: a stranger's copy with the id must not make the real message a "duplicate".
        await PostAsync(port, "/in", BuildMessage("PO*FORGED~", Stranger, Cert), id);
        await PostAsync(port, "/in", BuildMessage("PO*REAL~", Cert, Cert), id);

        routed.Should().ContainSingle();
    }

    [Fact]
    public async Task WithoutARepository_EveryCopyIsDelivered()
    {
        var (context, routed, port) = await StartReceiver(null);
        await using var _ = context;
        var message = BuildMessage("PO*TWICE~", Cert, Cert);
        var id = new Dictionary<string, string?> { ["Message-ID"] = "<twice@kit>" };

        await PostAsync(port, "/in", message, id);
        await PostAsync(port, "/in", message, id);

        routed.Should().HaveCount(2);
    }

    [Fact]
    public async Task UnknownRepository_StopsTheReceiverFromStarting()
    {
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver());
        var consumer = context.GetEndpoint(As2Dsl.Receive("/in").Host("127.0.0.1").Port(FreePort()).ConnectionFactory("recv").IdempotentRepository("nope"))
            .CreateConsumer(new DelegateProcessor(_ => { }));

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("nope");
    }
}
