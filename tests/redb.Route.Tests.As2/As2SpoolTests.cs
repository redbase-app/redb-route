using System.Text;
using FluentAssertions;
using redb.Route.As2;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Configuration;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;
using static redb.Route.Tests.As2.As2TestKit;

namespace redb.Route.Tests.As2;

/// <summary>
/// A received message goes through the core stream cache, as the AS4 receiver's does: the request, each decrypted and
/// decompressed stage, and the payload are spooled to a temporary file past the threshold instead of held in memory
/// (REVIEW-2026-09-28 R4). <c>streamBody</c> hands the spooled payload to the route as a <c>Stream</c>.
/// </summary>
public class As2SpoolTests
{
    private sealed class Receiver_
    {
        public required RouteContext Context { get; init; }
        public required int Port { get; init; }
        public required As2Endpoint Endpoint { get; init; }
    }

    private static async Task<Receiver_> Start(Func<redb.Route.Abstractions.IExchange, Task> route, bool streamBody, StreamCacheOptions? spool,
        bool compress = false)
    {
        var port = FreePort();
        var context = new RouteContext();
        if (spool is not null) context.AddService(typeof(StreamCacheOptions), spool);
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver(f => f.Compress = compress));
        var receive = As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv");
        if (streamBody) receive = receive.StreamBody();
        context.AddRoutes(r => r.From(receive).Process(e => route(e).GetAwaiter().GetResult()));
        await context.Start();
        return new Receiver_ { Context = context, Port = port, Endpoint = (As2Endpoint)context.GetEndpoint(receive) };
    }

    private static MdnParser.MdnResult Mdn(HttpResponseMessage response) => MdnParser.Parse(
        response.Content.Headers.ContentType?.ToString(),
        response.Content.Headers.TryGetValues("Content-Transfer-Encoding", out var v) ? string.Join(",", v) : null,
        response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult(), new As2CryptoEngine(), Cert);

    [Fact]
    public async Task LargePayload_WithStreamBody_IsSpooledToDisk_AndReadIntact()
    {
        var large = new string('x', 300_000);
        bool? spooled = null;
        string? read = null;
        var receiver = await Start(e =>
        {
            var cache = (StreamCache)e.In.Body!;
            spooled = cache.IsSpooled;
            read = new StreamReader(cache, leaveOpen: true).ReadToEnd();
            return Task.CompletedTask;
        }, streamBody: true, new StreamCacheOptions { SpoolThreshold = 4096 });
        await using var _ = receiver.Context;

        var mdn = Mdn(await PostAsync(receiver.Port, "/in", BuildMessage(large, Cert, Cert)));

        mdn.IsPositive.Should().BeTrue();
        spooled.Should().BeTrue();
        read.Should().Be(large);
    }

    [Fact]
    public async Task CompressedLargePayload_IsSpooled_AndItsMicStillMatches()
    {
        // Through the producer, so the MIC of compress-sign-encrypt is checked end to end against the spooled stages.
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddService(typeof(StreamCacheOptions), new StreamCacheOptions { SpoolThreshold = 4096 });
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver(f => f.Compress = true));
        context.AddToRegistry("them", Sender(f => f.Compress = true));
        bool? spooled = null;
        string? read = null;
        context.AddRoutes(r => r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv").StreamBody())
            .Process(e =>
            {
                var cache = (StreamCache)e.In.Body!;
                spooled = cache.IsSpooled;
                read = new StreamReader(cache, leaveOpen: true).ReadToEnd();
            }));
        await context.Start();
        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/in").ConnectionFactory("them")).CreateProducer();
        await producer.Start();

        var payload = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"LIN*{i}~"));
        var exchange = new Exchange(new Message(payload) { ContentType = "application/edi-x12" });
        await producer.Process(exchange);

        spooled.Should().BeTrue();
        read.Should().Be(payload);
        exchange.Out!.GetHeader<bool>(As2Headers.MdnConfirmed).Should().BeTrue("the MIC over the spooled content is the one the sender computed");
    }

    [Fact]
    public async Task StreamBody_IsReleasedWithTheExchange()
    {
        Stream? body = null;
        var receiver = await Start(e => { body = (Stream)e.In.Body!; return Task.CompletedTask; }, streamBody: true, spool: null);
        await using var _ = receiver.Context;

        Mdn(await PostAsync(receiver.Port, "/in", BuildMessage("PO*STREAM~", Cert, Cert))).IsPositive.Should().BeTrue();

        body.Should().NotBeNull();
        body!.CanRead.Should().BeFalse("Exchange.DisposeAsync closes a Stream body");
    }

    [Fact]
    public async Task WithoutStreamBody_TheBodyIsBytes_EvenWhenSpooled()
    {
        var large = new string('y', 200_000);
        object? body = null;
        var receiver = await Start(e => { body = e.In.Body; return Task.CompletedTask; }, streamBody: false,
            new StreamCacheOptions { SpoolThreshold = 4096 });
        await using var _ = receiver.Context;

        await PostAsync(receiver.Port, "/in", BuildMessage(large, Cert, Cert));

        body.Should().BeOfType<byte[]>().Which.Should().Equal(Encoding.UTF8.GetBytes(large));
    }

    [Fact]
    public async Task TheRequestItself_GoesThroughTheSpool()
    {
        // The spool directory is gone and the threshold is tiny: a request that is spooled cannot be read, and the
        // partner gets unexpected-processing-error with a reference, counted as an error. Held in memory, it would pass.
        var spool = new StreamCacheOptions
        {
            SpoolThreshold = 16,
            TempDirectory = Path.Combine(Path.GetTempPath(), "redb-as2-" + Guid.NewGuid().ToString("N"), "gone"),
        };
        var routed = 0;
        var receiver = await Start(_ => { Interlocked.Increment(ref routed); return Task.CompletedTask; }, streamBody: false, spool);
        await using var _ = receiver.Context;

        var mdn = Mdn(await PostAsync(receiver.Port, "/in", BuildMessage("PO*SPOOL~", Cert, Cert)));

        routed.Should().Be(0);
        mdn.Disposition.Should().EndWith("processed/error: unexpected-processing-error");
        ((redb.Route.Abstractions.IEndpointStatistics)receiver.Endpoint).Errors.Should().Be(1);
    }
}
