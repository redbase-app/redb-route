using System.Diagnostics;
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
/// Bounds and lifecycle (REVIEW-2026-09-28 R4, R13, R14): a request or response larger than the endpoint allows is
/// refused before it is buffered, and the MDN receiver drains, traces and labels its exchanges as the message
/// receiver does.
/// </summary>
public class As2LimitsTests
{
    [Fact]
    public async Task Message_LargerThanMaxRequestBodySize_IsRefusedWith413_AndNotRouted()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver());
        var routed = 0;
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv").MaxRequestBodySize(4096))
                .Process(_ => Interlocked.Increment(ref routed)));
        await context.Start();

        var big = BuildMessage(new string('X', 64 * 1024), Cert, Cert);
        var response = await PostAsync(port, "/in", big, expectContinue: true);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        routed.Should().Be(0);
    }

    [Fact]
    public async Task Message_WithinMaxRequestBodySize_IsRouted()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver());
        var routed = 0;
        context.AddRoutes(r =>
            r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv").MaxRequestBodySize(256 * 1024))
                .Process(_ => Interlocked.Increment(ref routed)));
        await context.Start();

        var response = await PostAsync(port, "/in", BuildMessage(new string('X', 64 * 1024), Cert, Cert));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        routed.Should().Be(1);
    }

    [Fact]
    public void MaxRequestBodySize_DefaultsTo100MB_AndMustBePositive()
    {
        new As2EndpointOptions().MaxRequestBodySize.Should().Be(100L * 1024 * 1024);
        var act = () => new As2EndpointOptions { MaxRequestBodySize = 0 }.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*maxRequestBodySize*");
    }

    [Fact]
    public async Task Mdn_LargerThanMaxRequestBodySize_IsRefusedWith413()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => { f.MdnMode = As2MdnMode.Async; f.AsyncMdnUrl = $"http://127.0.0.1:{port}/mdn"; }));
        var routed = 0;
        context.AddRoutes(r =>
            r.From(As2Dsl.ReceiveMdn("/mdn").Host("127.0.0.1").Port(port).ConnectionFactory("them").MaxRequestBodySize(4096))
                .Process(_ => Interlocked.Increment(ref routed)));
        await context.Start();

        using var client = new HttpClient();
        var content = new ByteArrayContent(new byte[64 * 1024]);
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/report; report-type=disposition-notification; boundary=x");
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mdn") { Content = content };
        request.Headers.ExpectContinue = true;   // refused before the body is sent, not reset mid-upload
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        routed.Should().Be(0);
    }

    [Fact]
    public async Task Send_WhoseResponseIsLargerThanMaxResponseBodySize_Fails_WithoutReadingIt()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var partner = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            await ctx.Request.InputStream.CopyToAsync(Stream.Null);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html";
            try { await ctx.Response.OutputStream.WriteAsync(new byte[256 * 1024]); ctx.Response.Close(); }
            catch (HttpListenerException) { /* the producer stopped reading: that is the point */ }
        });

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender());
        var producer = context.GetEndpoint(
            As2Dsl.Send($"http://127.0.0.1:{port}/as2").ConnectionFactory("them").MaxResponseBodySize(1024)).CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message("PO*BIG~") { ContentType = "application/edi-x12" }));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("maxResponseBodySize");
        await partner.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ── MDN receiver lifecycle ───────────────────────────────────────────────

    private static (string ContentType, string? Cte, byte[] Body) SignedMdn(string originalId) =>
        MdnBuilder.Build(originalId, "US", "THEM", new As2Mic("AAAA", "sha-256"), null, null, new As2CryptoEngine(), Cert);

    private static Task<HttpResponseMessage> PostMdn(HttpClient client, int port, (string ContentType, string? Cte, byte[] Body) mdn)
    {
        var content = new ByteArrayContent(mdn.Body);
        content.Headers.TryAddWithoutValidation("Content-Type", mdn.ContentType);
        if (mdn.Cte is not null) content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", mdn.Cte);
        return client.PostAsync($"http://127.0.0.1:{port}/mdn", content);
    }

    [Fact]
    public async Task MdnReceiver_Stop_WaitsForAReceiptAlreadyInTheRoute()
    {
        var port = FreePort();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => { f.MdnMode = As2MdnMode.Async; f.AsyncMdnUrl = $"http://127.0.0.1:{port}/mdn"; }));

        var slow = context.GetEndpoint(As2Dsl.ReceiveMdn("/mdn").Host("127.0.0.1").Port(port).ConnectionFactory("them").Build())
            .CreateConsumer(new redb.Route.Processors.DelegateProcessor(async (e, ct) =>
            {
                entered.TrySetResult();
                await release.Task;
                finished = true;
            }));
        // A second route keeps the listener alive after the first is stopped.
        context.AddToRegistry("recv", Receiver());
        var other = context.GetEndpoint(As2Dsl.Receive("/other").Host("127.0.0.1").Port(port).ConnectionFactory("recv").Build())
            .CreateConsumer(new redb.Route.Processors.DelegateProcessor(_ => { }));
        await slow.Start();
        await other.Start();

        using var client = new HttpClient();
        var call = PostMdn(client, port, SignedMdn("<drain@kit>"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = slow.Stop();
        (await Task.WhenAny(stop, Task.Delay(500)) == stop).Should().BeFalse("Stop must not return while a receipt is still in the route");

        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(15));
        finished.Should().BeTrue();
        (await call).StatusCode.Should().Be(HttpStatusCode.OK);
        await other.Stop();
    }

    [Fact]
    public async Task MdnReceiver_LabelsTheExchange_AndTracesTheRequest()
    {
        var spans = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "redb.Route",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(activityListener);

        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => { f.MdnMode = As2MdnMode.Async; f.AsyncMdnUrl = $"http://127.0.0.1:{port}/mdn"; }));
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string? Remote, string? Partner)>();
        context.AddRoutes(r =>
            r.From(As2Dsl.ReceiveMdn("/mdn").Host("127.0.0.1").Port(port).ConnectionFactory("them"))
                .Process(e => seen.Enqueue((e.In.GetHeader<string>(As2Headers.RemoteAddress), e.In.GetHeader<string>(As2Headers.PartnerName)))));
        await context.Start();

        using var client = new HttpClient();
        (await PostMdn(client, port, SignedMdn("<label@kit>"))).StatusCode.Should().Be(HttpStatusCode.OK);

        seen.Should().ContainSingle().Which.Should().Be(("127.0.0.1", "them"));
        // The span ends when the handler returns, just after the 200 the client already has.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        bool Traced() { lock (spans) return spans.Any(a => a.OperationName == "/mdn receive" && a.Kind == ActivityKind.Consumer); }
        while (!Traced() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Traced().Should().BeTrue();
    }
}
