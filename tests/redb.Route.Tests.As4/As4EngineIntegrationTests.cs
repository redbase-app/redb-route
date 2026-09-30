using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.As4;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Processors;
using As4Dsl = redb.Route.As4.Fluent.As4;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф8: the AS4 connector in the engine's cross-cutting mechanics, each after the neighbour test it copies —
/// admission limits (<c>As2ConcurrencyLimitOptionsTests</c>, <c>HttpConcurrencyLimitTests</c>), the shared host a
/// scan-loading module host provides (<c>SoapComponentSharedHostFromDiTests</c>), the drain on stop
/// (<c>SoapStopDrainsInflightTests</c>), statistics once per event (<c>As2ReceiveTests</c>, <c>HttpConcurrencyLimitTests</c>),
/// one trace across sender and receiver (<c>As2ReceiveTests.Loopback_EmitsLinkedProducerAndConsumerSpans</c>), and the
/// answer after a <c>.Transacted()</c> unit of work.
/// </summary>
public class As4EngineIntegrationTests
{
    private static readonly ActivitySource TraceTestSource = new("redb.Route.Tests.As4.Trace");
    private static readonly X509Certificate2 NodeA = As4TestMessages.KeyPair("CN=a.as4.test");
    private static readonly X509Certificate2 NodeB = As4TestMessages.KeyPair("CN=b.as4.test");
    private const string Payload = "<invoice xmlns=\"urn:example\"><total>42</total></invoice>";

    // ── Admission limits ─────────────────────────────────────────────────────

    [Fact]
    public void Limits_UriAndDsl_CarryTheOptions()
    {
        string built = As4Dsl.Receive("/as4/in").Port(4090).ConnectionFactory("b").IdempotentRepository("dedup")
            .MaxConcurrentRequests(2, queue: 4).RejectStatusCode(503).RetryAfterSeconds(0);

        built.Should().Contain("maxConcurrentRequests=2").And.Contain("requestQueueLimit=4")
            .And.Contain("rejectStatusCode=503").And.Contain("retryAfterSeconds=0");

        var endpoint = (As4Endpoint)new As4Component().CreateEndpoint(EndpointUriParser.Parse(built));
        endpoint.EndpointOptions.MaxConcurrentRequests.Should().Be(2);
        endpoint.EndpointOptions.RequestQueueLimit.Should().Be(4);
        endpoint.EndpointOptions.RejectStatusCode.Should().Be(503);
        endpoint.EndpointOptions.RetryAfterSeconds.Should().Be(0);
    }

    [Fact]
    public void Limits_QueueWithoutLimit_FailsLoud()
    {
        var act = () => new As4Component().CreateEndpoint(EndpointUriParser.Parse("as4:/in?connectionFactory=b&requestQueueLimit=3"));
        act.Should().Throw<ArgumentException>().WithMessage("*requestQueueLimit*maxConcurrentRequests*");
    }

    [Fact]
    public async Task Limits_OverTheLimit_IsShedWith503_CountedOnceAsRejected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = TestPort();
        await using var context = NewContext();
        var uri = Receive(port).MaxConcurrentRequests(1).Build();
        context.AddRoutes(r => r.From(uri).Process(async (_, _) => { entered.TrySetResult(); await release.Task; }));
        await context.Start();

        var first = Post(port);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var shed = await PostResponse(port);
        release.TrySetResult();
        (await first).Receipt.Should().BeTrue();

        shed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "an AS4 sender retries on 503");
        var stats = (IEndpointStatistics)context.GetEndpoint(uri);
        stats.Rejected.Should().Be(1, "each shed request is counted once");
        stats.Errors.Should().Be(0, "shedding is not a processing error");
    }

    // ── The shared host of a module host ─────────────────────────────────────

    [Fact]
    public void SharedHost_FromTheContextsContainer_IsUsedWhenNoneWasAssigned()
    {
        var shared = new SharedHttpServerManager();
        var component = new As4Component();
        using var context = new RouteContext();
        context.SetServiceProvider(new ServiceCollection().AddSingleton(shared).BuildServiceProvider());
        context.AddComponent(component);

        component.Server.Should().BeSameAs(shared);
    }

    [Fact]
    public void SharedHost_AssignedWinsOverTheContainer()
    {
        var assigned = new SharedHttpServerManager();
        var component = new As4Component { ServerManager = assigned };
        using var context = new RouteContext();
        context.SetServiceProvider(new ServiceCollection().AddSingleton(new SharedHttpServerManager()).BuildServiceProvider());
        context.AddComponent(component);

        component.Server.Should().BeSameAs(assigned);
    }

    [Fact]
    public void SharedHost_AsAContextService_IsFound()
    {
        var shared = new SharedHttpServerManager();
        var component = new As4Component();
        using var context = new RouteContext();
        context.AddService(typeof(SharedHttpServerManager), shared);
        context.AddComponent(component);

        component.Server.Should().BeSameAs(shared);
    }

    [Fact]
    public async Task SharedHost_OneListener_ServesAnHttpRouteAndAnAs4RouteOnOnePort()
    {
        // As a scan-loaded module host runs them: both components find the one manager, neither owns a server.
        var port = TestPort();
        await using var shared = new SharedHttpServerManager();
        await using var context = NewContext();
        context.AddService(typeof(SharedHttpServerManager), shared);
        context.AddComponent(new HttpComponent());
        context.AddRoutes(r =>
        {
            r.From($"http://127.0.0.1:{port}/ping?inOut=true").SetBody("pong");
            r.From(Receive(port)).Process(_ => { });
        });
        await context.Start();

        using var http = new HttpClient();
        (await http.GetStringAsync($"http://127.0.0.1:{port}/ping")).Should().Be("pong");
        (await Post(port)).Receipt.Should().BeTrue();
    }

    // ── Stop ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_WaitsForAMessageAlreadyInTheRoute_WhileThePortStaysBusy()
    {
        var port = TestPort();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        await using var context = NewContext();

        var slow = context.GetEndpoint(Receive(port)).CreateConsumer(new DelegateProcessor(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            finished = true;
        }));
        // A second route keeps the listener up after the first one stops — the case where nothing used to wait.
        var other = context.GetEndpoint(As4Dsl.Receive("/as4/other").Host("127.0.0.1").Port(port).ConnectionFactory("b").IdempotentRepository("dedup"))
            .CreateConsumer(new DelegateProcessor(_ => { }));
        await slow.Start();
        await other.Start();

        var call = Post(port);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = slow.Stop();
        (await Task.WhenAny(stop, Task.Delay(500)) == stop).Should().BeFalse("Stop must not return while a message of this route is in the pipeline");

        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        finished.Should().BeTrue();
        (await call).Receipt.Should().BeTrue("the message in flight is answered, not cut");
        await other.Stop();
    }

    // ── Statistics ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Statistics_EachEventIsCountedOnce()
    {
        var port = TestPort();
        var fail = false;
        await using var context = NewContext();
        var uri = Receive(port).Build();
        context.AddRoutes(r => r.From(uri).Process(_ => { if (fail) throw new InvalidOperationException("route fails"); }));
        await context.Start();
        var stats = (IEndpointStatistics)context.GetEndpoint(uri);

        (await Post(port)).Receipt.Should().BeTrue();
        stats.MessagesIn.Should().Be(1);
        stats.Errors.Should().Be(0);

        fail = true;
        (await Post(port)).ErrorCode.Should().Be("EBMS:0004");
        stats.MessagesIn.Should().Be(2);
        stats.Errors.Should().Be(1, "a route failure is counted by the engine, not again by the connector");

        using var unsigned = await PostResponse(port, sign: false);
        stats.MessagesIn.Should().Be(2, "a message refused before the route is not a message in");
        stats.Errors.Should().Be(2, "the refusal is one error");
    }

    [Fact]
    public async Task Statistics_TheSenderCountsOneMessageOut()
    {
        var port = TestPort();
        await using var context = NewContext();
        context.AddRoutes(r =>
        {
            r.From(Receive(port)).Process(_ => { });
            r.From("direct://out").To(Send(port));
        });
        await context.Start();
        var producer = context.GetEndpoint("direct://out").CreateProducer();
        await producer.Start();

        await producer.Process(NewExchange());

        ((IEndpointStatistics)context.GetEndpoint(Send(port))).MessagesOut.Should().Be(1);
    }

    // ── Tracing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Trace_SenderAndReceiverSpans_ShareOneTrace()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == redb.Route.Telemetry.RouteActivitySource.SourceName || s.Name == TraceTestSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var port = TestPort();
        await using var context = NewContext();
        var receive = Receive(port).Build();
        context.AddRoutes(r => r.From(receive).Process(_ => { }));
        await context.Start();
        var producer = context.GetEndpoint(Send(port)).CreateProducer();
        await producer.Start();
        ActivityTraceId traceId;
        // The listener sees every test's spans in the process: this test's own root tells its trace apart (as SpanCapture does).
        using (var root = TraceTestSource.StartActivity("test"))
        {
            await producer.Process(NewExchange());
            traceId = root!.TraceId;
        }

        Activity? send, received;
        lock (spans)
        {
            send = spans.LastOrDefault(a => a.OperationName == "b send" && a.TraceId == traceId);
            received = spans.LastOrDefault(a => a.OperationName == "/as4/in receive" && a.TraceId == send?.TraceId);
        }
        send.Should().NotBeNull();
        send!.Kind.Should().Be(ActivityKind.Client);
        received.Should().NotBeNull("the receiver continues the sender's trace from traceparent");
        received!.Kind.Should().Be(ActivityKind.Consumer);
        received.GetTagItem("redb.route.endpoint").Should().NotBeNull("as StartTransportSpan tags every transport span");
    }

    // The core tracing contract (RouteTelemetryExtensions, redb.Route 0b5658f0), as the HTTP transport uses it.

    private static (ActivityTraceId TraceId, ActivitySpanId SpanId, string Header) Caller()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return (traceId, spanId, $"00-{traceId}-{spanId}-01");
    }

    [Fact]
    public async Task Trace_ReceivedMessage_WithTraceparent_ContinuesTheSendersTrace_OnANamedSpan()
    {
        var port = TestPort();
        await using var context = NewContext();
        context.AddRoutes(r => r.From(Receive(port)).Process(_ => { }));
        await context.Start();
        using var probe = redb.Route.Tests.Telemetry.RouteTelemetryProbe.ForEndpointContaining($"port={port}");
        var caller = Caller();

        using var response = await PostResponse(port, headers: new Dictionary<string, string> { ["traceparent"] = caller.Header });

        var span = probe.Activities.Single(a => a.Kind == ActivityKind.Consumer);
        span.TraceId.Should().Be(caller.TraceId);
        span.ParentSpanId.Should().Be(caller.SpanId);
        span.DisplayName.Should().Be("/as4/in receive", "the span names its destination, as the messaging conventions do");
    }

    [Fact]
    public async Task Trace_ReceivedMessage_WithoutTraceparent_IsARootSpan()
    {
        var port = TestPort();
        await using var context = NewContext();
        context.AddRoutes(r => r.From(Receive(port)).Process(_ => { }));
        await context.Start();
        using var probe = redb.Route.Tests.Telemetry.RouteTelemetryProbe.ForEndpointContaining($"port={port}");

        using var response = await PostResponse(port);

        var span = probe.Activities.Single(a => a.Kind == ActivityKind.Consumer);
        span.ParentSpanId.Should().Be(default(ActivitySpanId));
        span.Parent.Should().BeNull();
    }

    [Fact]
    public async Task Trace_TheSendersBaggage_ReachesTheRoute()
    {
        var port = TestPort();
        await using var context = NewContext();
        var baggage = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        context.AddRoutes(r => r.From(Receive(port)).Process(_ => baggage.Enqueue(Activity.Current?.GetBaggageItem("tenant"))));
        await context.Start();
        using var probe = redb.Route.Tests.Telemetry.RouteTelemetryProbe.ForEndpointContaining($"port={port}");   // the baggage rides on the span
        var headers = new Dictionary<string, string>();
        using (var send = new Activity("caller").Start())
        {
            send.AddBaggage("tenant", "t1");
            redb.Route.Telemetry.RouteTelemetryExtensions.InjectTraceContext(send, headers, static (h, name, value) => h[name] = value);
        }

        using var response = await PostResponse(port, headers: headers);

        baggage.Should().ContainSingle().Which.Should().Be("t1");
    }

    [Fact]
    public async Task Trace_ASend_CarriesTheContextOfItsOwnSpan_AndAMissingReceiptMarksItRed()
    {
        // A partner that records the traceparent it gets and answers without a receipt.
        var port = TestPort();
        using var partner = new HttpListener();
        partner.Prefixes.Add($"http://127.0.0.1:{port}/");
        partner.Start();
        string? seen = null;
        var serving = Task.Run(async () =>
        {
            var ctx = await partner.GetContextAsync();
            seen = ctx.Request.Headers["traceparent"];
            await ctx.Request.InputStream.CopyToAsync(Stream.Null);
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });
        await using var context = NewContext();
        await context.Start();
        var producer = context.GetEndpoint(Send(port)).CreateProducer();
        await producer.Start();
        using var probe = redb.Route.Tests.Telemetry.RouteTelemetryProbe.ForEndpointContaining($":{port}/");

        var act = () => producer.Process(NewExchange());

        await act.Should().ThrowAsync<As4ReceiptException>();
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
        var send = probe.Activities.Single(a => a.Kind == ActivityKind.Client);
        send.DisplayName.Should().Be("b send");
        seen.Should().Be($"00-{send.TraceId}-{send.SpanId}-01");
        send.Status.Should().Be(ActivityStatusCode.Error, "a response without a receipt is a failed send");
    }

    [Fact]
    public async Task Trace_TelemetryOff_OpensNoSpan_AndTheCallersContextStillReachesThePartner()
    {
        var port = TestPort();
        using var partner = new HttpListener();
        partner.Prefixes.Add($"http://127.0.0.1:{port}/");
        partner.Start();
        string? seen = null;
        var serving = Task.Run(async () =>
        {
            var ctx = await partner.GetContextAsync();
            seen = ctx.Request.Headers["traceparent"];
            await ctx.Request.InputStream.CopyToAsync(Stream.Null);
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });
        using var callerListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == TraceTestSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(callerListener);
        await using var context = NewContext(new redb.Route.Configuration.RouteEngineOptions { EnableTelemetry = false });
        await context.Start();
        var producer = context.GetEndpoint(Send(port)).CreateProducer();
        await producer.Start();
        using var probe = redb.Route.Tests.Telemetry.RouteTelemetryProbe.ForEndpointContaining($":{port}/");

        ActivityTraceId traceId;
        using (var root = TraceTestSource.StartActivity("caller"))
        {
            traceId = root!.TraceId;
            var act = () => producer.Process(NewExchange());
            await act.Should().ThrowAsync<As4ReceiptException>();   // the partner answers without a receipt
        }
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

        probe.Activities.Should().BeEmpty();
        seen.Should().Contain(traceId.ToString(), "a redb hop with tracing off does not cut the trace around it");
    }

    // ── Transactions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Transacted_Commit_IsAcknowledgedWithAReceipt()
    {
        var port = TestPort();
        await using var context = NewContext();
        context.AddRoutes(r => r.From(Receive(port)).Transacted().Process(_ => { }).End());
        await context.Start();

        (await Post(port)).Receipt.Should().BeTrue();
    }

    [Fact]
    public async Task Transacted_Rollback_IsAnsweredWithAnError_NotAReceipt()
    {
        var port = TestPort();
        await using var context = NewContext();
        context.AddRoutes(r => r.From(Receive(port)).Transacted().Process(_ => throw new InvalidOperationException("rolls back")).End());
        await context.Start();

        var (_, errorCode, receipt) = await Post(port);

        errorCode.Should().Be("EBMS:0004");
        receipt.Should().BeFalse();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────


    private static int TestPort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    private static redb.Route.As4.Fluent.As4Builder Receive(int port) =>
        As4Dsl.Receive("/as4/in").Host("127.0.0.1").Port(port).ConnectionFactory("b").IdempotentRepository("dedup");

    private static string Send(int port) =>
        As4Dsl.Send($"http://127.0.0.1:{port}/as4/in").ConnectionFactory("a").Partner("b").Timeout(15000);

    private static RouteContext NewContext(redb.Route.Configuration.RouteEngineOptions? options = null)
    {
        var context = new RouteContext(options: options ?? new redb.Route.Configuration.RouteEngineOptions());
        context.AddComponent(new As4Component());
        context.AddIdempotentRepository("dedup", new InMemoryIdempotentRepository());
        context.AddToRegistry("b", Node("b", NodeB, Partner("a", NodeA)));
        context.AddToRegistry("a", Node("a", NodeA, Partner("b", NodeB)));
        return context;
    }

    private static As4Partner Partner(string name, X509Certificate2 certificate) => new()
    {
        Name = name,
        PartyId = "urn:redb:" + name,
        Service = "urn:example",
        Action = "Submit",
        PartnerSigningCertificates = { As4TestMessages.PublicOnly(certificate) },
        PartnerEncryptionCertificate = As4TestMessages.PublicOnly(certificate),
    };

    private static As4ConnectionFactory Node(string name, X509Certificate2 key, As4Partner partner) => new()
    {
        OurPartyId = "urn:redb:" + name,
        ExternalHostName = name + ".example",
        SigningCertificate = key,
        DecryptionCertificates = { key },
        Partners = { partner },
    };

    private static Exchange NewExchange()
    {
        var exchange = new Exchange(new Message(Payload) { ContentType = "text/xml" });
        exchange.In.Headers[As4Headers.OriginalSender] = "C1";
        exchange.In.Headers[As4Headers.FinalRecipient] = "C4";
        return exchange;
    }

    private static async Task<HttpResponseMessage> PostResponse(int port, bool sign = true, IReadOnlyDictionary<string, string>? headers = null)
    {
        const string contentId = "p1@a.example";
        using var message = SwaMessage.Create(As4TestMessages.UserMessage(Guid.NewGuid().ToString("N") + "@a.example", contentId,
            from: "urn:redb:a", to: "urn:redb:b", service: "urn:example", action: "Submit"));
        message.AddPart(contentId, "application/gzip", As4TestMessages.Gzip(Encoding.UTF8.GetBytes(Payload)));
        if (sign)
        {
            As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
            As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        }
        var (contentType, body) = message.Write();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/as4/in") { Content = content };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);
        return await http.SendAsync(request);
    }

    private static async Task<(HttpStatusCode Status, string? ErrorCode, bool Receipt)> Post(int port)
    {
        using var response = await PostResponse(port);
        var answer = SwaMessage.Read(response.Content.Headers.ContentType!.ToString(), await response.Content.ReadAsByteArrayAsync(), 1024 * 1024);
        var signals = MessagingReader.Read(answer.Envelope).SignalMessages;
        return (response.StatusCode, signals.SelectMany(s => s.Errors).FirstOrDefault()?.ErrorCode, signals.Any(s => s.IsReceipt));
    }
}
