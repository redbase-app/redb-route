using System.Diagnostics;
using System.Net;
using FluentAssertions;
using redb.Route.As2;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;
using As2Dsl = redb.Route.As2.Fluent.As2;
using static redb.Route.Tests.As2.As2TestKit;

namespace redb.Route.Tests.As2;

/// <summary>
/// The AS2 transports on the core tracing contract (<see cref="RouteTelemetryExtensions"/>, redb.Route 0b5658f0), as
/// the HTTP transport uses it: a received message opens a Consumer span continuing the sender's trace, or a root; the
/// sender's baggage reaches the route; a send writes the context of its own span to the request, replacing a copied
/// header; a failed send is red; <c>EnableTelemetry=false</c> opens none of these spans.
/// </summary>
public class As2TracingTests
{
    private static (ActivityTraceId TraceId, ActivitySpanId SpanId, string Header) Caller()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return (traceId, spanId, $"00-{traceId}-{spanId}-01");
    }

    private static async Task<(RouteContext Context, int Port, List<string?> Baggage)> StartReceiver(RouteEngineOptions? options = null)
    {
        var port = FreePort();
        var context = new RouteContext(options: options ?? new RouteEngineOptions());
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver());
        var baggage = new List<string?>();
        context.AddRoutes(r => r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv"))
            .Process(_ => { lock (baggage) baggage.Add(Activity.Current?.GetBaggageItem("tenant")); }));
        await context.Start();
        return (context, port, baggage);
    }

    private static Activity ReceiveSpan(RouteTelemetryProbe probe)
        => probe.Activities.Single(a => a.Kind == ActivityKind.Consumer);

    [Fact]
    public async Task Received_message_with_traceparent_continues_the_senders_trace_on_a_named_span()
    {
        var (context, port, _) = await StartReceiver();
        await using var _ = context;
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"port={port}");
        var caller = Caller();

        (await PostAsync(port, "/in", BuildMessage("PO*TRACE~", Cert, Cert),
            new Dictionary<string, string?> { ["traceparent"] = caller.Header })).StatusCode.Should().Be(HttpStatusCode.OK);

        var span = ReceiveSpan(probe);
        span.TraceId.Should().Be(caller.TraceId);
        span.ParentSpanId.Should().Be(caller.SpanId);
        span.DisplayName.Should().Be("/in receive", "the span names its destination, as the messaging conventions do");
        RouteTelemetryProbe.Tag(span, RouteTelemetryProbe.EndpointTag).Should().Contain($"port={port}");
    }

    [Fact]
    public async Task Received_message_without_traceparent_is_a_root_span()
    {
        var (context, port, _) = await StartReceiver();
        await using var _ = context;
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"port={port}");

        await PostAsync(port, "/in", BuildMessage("PO*ROOT~", Cert, Cert));

        var span = ReceiveSpan(probe);
        span.ParentSpanId.Should().Be(default(ActivitySpanId));
        span.Parent.Should().BeNull();
    }

    [Fact]
    public async Task The_senders_baggage_reaches_the_route()
    {
        var (context, port, baggage) = await StartReceiver();
        await using var _ = context;
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"port={port}");   // the baggage rides on the span, which needs a listener
        var headers = new Dictionary<string, string?>();
        using (var send = new Activity("caller").Start())
        {
            send.AddBaggage("tenant", "t1");
            RouteTelemetryExtensions.InjectTraceContext(send, headers, static (h, name, value) => h[name] = value);
        }

        await PostAsync(port, "/in", BuildMessage("PO*BAGGAGE~", Cert, Cert), headers);

        baggage.Should().ContainSingle().Which.Should().Be("t1");
    }

    [Fact]
    public async Task A_send_writes_the_context_of_its_own_span_replacing_a_copied_traceparent()
    {
        // From(http) -> To(as2): the caller's traceparent is an exchange header and was bridged onto the request, so
        // HttpClient did not write its own and the partner saw the previous hop.
        using var trap = new As2Trap(FreePort());
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => f.MdnMode = As2MdnMode.None));
        var producer = context.GetEndpoint(As2Dsl.Send(trap.Url("/as2")).ConnectionFactory("them")).CreateProducer();
        await producer.Start();
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{trap.Port}/");
        var caller = Caller();
        var message = new Message("PO*HOP~") { ContentType = "application/edi-x12" };
        message.Headers["traceparent"] = caller.Header;

        await producer.Process(new Exchange(message));

        (await trap.HitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        trap.Requests.TryPeek(out var hit);
        var send = probe.Activities.Single(a => a.Kind == ActivityKind.Client);
        hit.Headers["traceparent"].Should().Be($"00-{send.TraceId}-{send.SpanId}-01");
    }

    [Fact]
    public async Task A_send_the_partner_does_not_answer_within_the_timeout_marks_its_span_red()
    {
        // HttpClient reports its own timeout as TaskCanceledException: a failure of the send, not a cancellation by the caller.
        var port = FreePort();
        using var silent = new HttpListener();
        silent.Prefixes.Add($"http://127.0.0.1:{port}/");
        silent.Start();
        var accepted = silent.GetContextAsync();   // takes the request and never answers
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => f.MdnMode = As2MdnMode.None));
        var producer = context.GetEndpoint($"as2://127.0.0.1:{port}/as2?connectionFactory=them&timeout=300").CreateProducer();
        await producer.Start();
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{port}/");

        var act = () => producer.Process(new Exchange(new Message("PO*SLOW~")));

        await act.Should().ThrowAsync<TaskCanceledException>();
        probe.Activities.Single(a => a.Kind == ActivityKind.Client).Status.Should().Be(ActivityStatusCode.Error);
        (await accepted).Response.Abort();
    }

    [Fact]
    public async Task A_send_cancelled_by_the_caller_is_not_marked_as_a_failure()
    {
        var port = FreePort();
        using var silent = new HttpListener();
        silent.Prefixes.Add($"http://127.0.0.1:{port}/");
        silent.Start();
        var accepted = silent.GetContextAsync();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => f.MdnMode = As2MdnMode.None));
        var producer = context.GetEndpoint($"as2://127.0.0.1:{port}/as2?connectionFactory=them&timeout=30000").CreateProducer();
        await producer.Start();
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{port}/");
        using var cts = new CancellationTokenSource();

        var sending = producer.Process(new Exchange(new Message("PO*STOP~")), cts.Token);
        var request = await accepted;   // the request reached the partner, which does not answer; now the caller gives up
        cts.Cancel();
        var act = () => sending;

        await act.Should().ThrowAsync<OperationCanceledException>();
        probe.Activities.Single(a => a.Kind == ActivityKind.Client).Status.Should().NotBe(ActivityStatusCode.Error,
            "the caller stopped the send; nothing failed");
        request.Response.Abort();
    }

    [Fact]
    public async Task A_failed_send_marks_its_span_red()
    {
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f => f.MdnMode = As2MdnMode.None));
        var closed = FreePort();
        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{closed}/as2").ConnectionFactory("them")).CreateProducer();
        await producer.Start();
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{closed}/");

        var act = () => producer.Process(new Exchange(new Message("PO*DOWN~")));

        await act.Should().ThrowAsync<HttpRequestException>();
        probe.Activities.Single(a => a.Kind == ActivityKind.Client).Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_and_still_passes_the_senders_context_on()
    {
        using var trap = new As2Trap(FreePort());
        var port = FreePort();
        await using var context = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = false });
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver());
        context.AddToRegistry("next", Sender(f => { f.As2From = "US"; f.As2To = "NEXT"; f.MdnMode = As2MdnMode.None; }));
        context.AddRoutes(r => r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv"))
            .To(As2Dsl.Send(trap.Url("/next")).ConnectionFactory("next")));
        await context.Start();
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"{port}");
        using var sendProbe = RouteTelemetryProbe.ForEndpointContaining($":{trap.Port}/");
        var caller = Caller();

        await PostAsync(port, "/in", BuildMessage("PO*OFF~", Cert, Cert), new Dictionary<string, string?> { ["traceparent"] = caller.Header });

        (await trap.HitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        trap.Requests.TryPeek(out var hit);
        hit.Headers["traceparent"].Should().Contain(caller.TraceId.ToString(), "a redb hop with tracing off does not cut the trace around it");
        probe.Activities.Should().BeEmpty();
        sendProbe.Activities.Should().BeEmpty();
    }

    // ── Failures on the receive span (review of 838764a3) ────────────────────

    private static async Task<(RouteContext Context, int Port)> StartMdnReceiver(Action<As2ConnectionFactory>? configure = null,
        bool routeFails = false, long? maxBody = null)
    {
        var port = FreePort();
        var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("them", Sender(f =>
        {
            f.MdnMode = As2MdnMode.Async;
            f.AsyncMdnUrl = $"http://127.0.0.1:{port}/mdn";
            configure?.Invoke(f);
        }));
        var receive = As2Dsl.ReceiveMdn("/mdn").Host("127.0.0.1").Port(port).ConnectionFactory("them");
        if (maxBody is { } limit) receive = receive.MaxRequestBodySize(limit);
        context.AddRoutes(r => r.From(receive).Process(_ => { if (routeFails) throw new InvalidOperationException("route failed"); }));
        await context.Start();
        return (context, port);
    }

    private static async Task<HttpStatusCode> PostMdn(int port, (string ContentType, string? Cte, byte[] Body) mdn, bool expectContinue = false)
    {
        using var client = new HttpClient();
        var content = new ByteArrayContent(mdn.Body);
        content.Headers.TryAddWithoutValidation("Content-Type", mdn.ContentType);
        if (mdn.Cte is not null) content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", mdn.Cte);
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mdn") { Content = content };
        if (expectContinue) request.Headers.ExpectContinue = true;
        return (await client.SendAsync(request)).StatusCode;
    }

    private static (string ContentType, string? Cte, byte[] Body) SignedMdn(bool signed = true) =>
        redb.Route.As2.Mdn.MdnBuilder.Build("<m@kit>", "US", "THEM", new redb.Route.As2.Crypto.As2Mic("AAAA", "sha-256"),
            signer: signed ? new redb.Route.As2.Crypto.As2CryptoEngine() : null, signerCert: signed ? Cert : null);

    private static Activity MdnSpan(RouteTelemetryProbe probe) => probe.Activities.Single(a => a.Kind == ActivityKind.Consumer);

    [Fact]
    public async Task MdnReceiver_AnUnreadableMdn_IsRefused_AndMarksTheSpanRed()
    {
        var (context, port) = await StartMdnReceiver();
        await using var _ = context;
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"port={port}");

        // A multipart/signed without its signature part: verifying it throws, the MDN cannot be read.
        var broken = "--b\r\nContent-Type: text/plain\r\n\r\nno signature follows\r\n--b--\r\n"u8.ToArray();
        (await PostMdn(port, ("multipart/signed; boundary=b; protocol=\"application/pkcs7-signature\"; micalg=sha-256", null, broken)))
            .Should().Be(HttpStatusCode.BadRequest);

        MdnSpan(probe).Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task MdnReceiver_AReceiptThePolicyRefuses_MarksTheSpanRed()
    {
        var (context, port) = await StartMdnReceiver(f => f.RequireValidMdn = true);
        await using var _ = context;
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"port={port}");

        (await PostMdn(port, SignedMdn(signed: false))).Should().Be(HttpStatusCode.BadRequest);

        MdnSpan(probe).Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task MdnReceiver_AFailedRoute_MarksTheSpanRed()
    {
        var (context, port) = await StartMdnReceiver(routeFails: true);
        await using var _ = context;
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"port={port}");

        (await PostMdn(port, SignedMdn())).Should().Be(HttpStatusCode.OK, "the partner delivered its receipt; the route's failure is the route's");

        MdnSpan(probe).Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task MdnReceiver_ABodyOverTheLimit_MarksTheSpanRed()
    {
        var (context, port) = await StartMdnReceiver(maxBody: 1024);
        await using var _ = context;
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"port={port}");

        (await PostMdn(port, ("multipart/report; boundary=x", null, new byte[64 * 1024]), expectContinue: true))
            .Should().Be(HttpStatusCode.RequestEntityTooLarge);

        MdnSpan(probe).Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Receiver_ABodyOverTheLimit_MarksTheSpanRed()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("recv", Receiver());
        context.AddRoutes(r => r.From(As2Dsl.Receive("/in").Host("127.0.0.1").Port(port).ConnectionFactory("recv").MaxRequestBodySize(1024))
            .Process(_ => { }));
        await context.Start();
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"port={port}");

        (await PostAsync(port, "/in", BuildMessage(new string('X', 64 * 1024), Cert, Cert), expectContinue: true))
            .StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        ReceiveSpan(probe).Status.Should().Be(ActivityStatusCode.Error);
    }
}
