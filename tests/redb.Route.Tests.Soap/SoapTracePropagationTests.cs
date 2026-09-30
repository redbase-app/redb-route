using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Soap;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// A SOAP request is inbound traffic like an HTTP request: the consumer's server span covers the whole request, the
/// ones refused before the envelope is read included, continues the caller's trace from <c>traceparent</c> and puts the
/// caller's baggage back; a failed route marks it red. The producer's client span is an RPC span (<c>rpc.method</c>, no
/// <c>messaging.*</c>) and the request carries its context, so the service's span is its child.
/// <c>EnableTelemetry=false</c> opens none of these spans.
/// </summary>
public sealed class SoapTracePropagationTests : IAsyncLifetime
{
    private const string Ping = "<Ping xmlns=\"urn:test\"><n>1</n></Ping>";
    private int _port;
    private int _offPort;
    private RouteContext _ctx = null!;
    private RouteContext _offCtx = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _port = global::redb.Route.Tests.Shared.TestPorts.Next();
        _offPort = global::redb.Route.Tests.Shared.TestPorts.Next();
        _client = new HttpClient();

        var port = _port;
        _ctx = new RouteContext();
        _ctx.AddComponent(new SoapComponent());
        _ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/traced").Host("127.0.0.1").Port(port)).RouteId($"soap-trace-{port}-traced")
                .Process(e => e.In.Body = "<Reply xmlns=\"urn:test\">ok</Reply>");
            r.From(SoapDsl.Listen("/fails").Host("127.0.0.1").Port(port)).RouteId($"soap-trace-{port}-fails")
                .Process(_ => throw new InvalidOperationException("route failed"));
            r.From(SoapDsl.Listen("/baggage").Host("127.0.0.1").Port(port)).RouteId($"soap-trace-{port}-baggage")
                .Process(e => e.In.Body = $"<Reply xmlns=\"urn:test\">{Activity.Current?.GetBaggageItem("tenant") ?? "none"}</Reply>");
            r.From($"direct://soap-trace-{port}-call")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/traced").Operation("Ping"));
            r.From(SoapDsl.Listen("/sender-fault").Host("127.0.0.1").Port(port))
                .Process(_ => throw new SoapFaultException("soap:Client", "no such order"));
            r.From(SoapDsl.Listen("/receiver-fault").Host("127.0.0.1").Port(port))
                .Process(_ => throw new SoapFaultException("soap:Server", "backend down"));
            r.From(SoapDsl.Listen("/declared-sender").Host("127.0.0.1").Port(port))
                .Process(e => e.In.Headers[SoapHeaders.FaultString] = "no such order");
            r.From(SoapDsl.Listen("/declared-receiver").Host("127.0.0.1").Port(port))
                .Process(e =>
                {
                    e.In.Headers[SoapHeaders.FaultString] = "backend down";
                    e.In.Headers[SoapHeaders.FaultCode] = "soap:Server";
                });
            r.From(SoapDsl.Listen("/cancelled").Host("127.0.0.1").Port(port))
                .Process(_ => throw new TaskCanceledException("a call inside the route timed out"));
            r.From(SoapDsl.Listen("/slow").Host("127.0.0.1").Port(port))
                .Process(async (_, ct) => await Task.Delay(5000, ct));
        });
        await _ctx.Start();

        var offPort = _offPort;
        _offCtx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = false });
        _offCtx.AddComponent(new SoapComponent());
        _offCtx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/traced").Host("127.0.0.1").Port(offPort))
                .Process(e => e.In.Body = "<Reply xmlns=\"urn:test\">ok</Reply>");
            r.From($"direct://soap-trace-{offPort}-call")
                .To(SoapDsl.Call($"http://127.0.0.1:{offPort}/traced").Operation("Ping"));
        });
        await _offCtx.Start();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _ctx.DisposeAsync();
        await _offCtx.DisposeAsync();
    }

    private static (ActivityTraceId TraceId, ActivitySpanId SpanId, string Header) Caller()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return (traceId, spanId, $"00-{traceId}-{spanId}-01");
    }

    /// <summary>POSTs a SOAP 1.1 envelope, outside any activity: only <paramref name="headers"/> carry a context.</summary>
    private async Task<HttpResponseMessage> Post(string path, string? contentType = null, string? body = null,
        params (string Name, string Value)[] headers)
    {
        var ambient = Activity.Current;
        Activity.Current = null;
        try
        {
            var envelope = body ?? $"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>{Ping}</soap:Body></soap:Envelope>";
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}{path}")
            {
                Content = new StringContent(envelope, Encoding.UTF8),
            };
            request.Content.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType ?? "text/xml; charset=utf-8");
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
            return await _client.SendAsync(request);
        }
        finally
        {
            Activity.Current = ambient;
        }
    }

    private RouteTelemetryProbe ServerSpans(string path)
        => new(a => a.Kind == ActivityKind.Server
                    && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag) is { } endpoint
                    && endpoint.Contains(_port.ToString(), StringComparison.Ordinal)
                    && endpoint.Contains(path, StringComparison.Ordinal));

    [Fact]
    public async Task A_request_opens_a_server_span_that_continues_the_callers_trace()
    {
        using var probe = ServerSpans("/traced");
        using var routes = RouteTelemetryProbe.ForRouteIdPrefix($"soap-trace-{_port}-traced");
        var caller = Caller();

        (await Post("/traced", headers: ("traceparent", caller.Header))).StatusCode.Should().Be(HttpStatusCode.OK);

        var server = probe.Activities.Should().ContainSingle().Subject;
        server.TraceId.Should().Be(caller.TraceId);
        server.ParentSpanId.Should().Be(caller.SpanId);
        routes.Activities.Should().ContainSingle().Which.ParentSpanId.Should().Be(server.SpanId,
            "the route runs inside the request");
    }

    [Fact]
    public async Task A_request_refused_before_the_envelope_is_read_has_its_span_too()
    {
        using var probe = ServerSpans("/traced");

        var response = await Post("/traced", "multipart/related; boundary=BND", "not a multipart body");

        response.IsSuccessStatusCode.Should().BeFalse();
        probe.Activities.Should().ContainSingle()
            .Which.Status.Should().NotBe(ActivityStatusCode.Error, "a malformed request is the caller's mistake, answered correctly");
    }

    [Fact]
    public async Task The_callers_baggage_reaches_the_route()
    {
        using var probe = ServerSpans("/baggage");
        var headers = new Dictionary<string, string>();
        using (var send = new Activity("caller").Start())
        {
            send.AddBaggage("tenant", "t1");
            RouteTelemetryExtensions.InjectTraceContext(send, headers, static (h, name, value) => h[name] = value);
        }

        var response = await Post("/baggage", headers: headers.Select(h => (h.Key, h.Value)).ToArray());

        (await response.Content.ReadAsStringAsync()).Should().Contain(">t1<");
    }

    [Fact]
    public async Task A_failed_route_marks_the_server_span_red()
    {
        using var probe = ServerSpans("/fails");

        await Post("/fails");

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Theory]
    [InlineData("/sender-fault")]
    [InlineData("/declared-sender")]
    public async Task A_sender_fault_the_route_chose_is_an_answer_not_a_failure(string path)
    {
        using var probe = ServerSpans(path);

        var response = await Post(path);

        (await response.Content.ReadAsStringAsync()).Should().Contain("Fault");
        probe.Activities.Should().ContainSingle().Which.Status.Should().NotBe(ActivityStatusCode.Error,
            "a Client/Sender fault is the caller's mistake, answered correctly");
    }

    [Theory]
    [InlineData("/receiver-fault")]
    [InlineData("/declared-receiver")]
    public async Task A_receiver_fault_marks_the_server_span_red(string path)
    {
        using var probe = ServerSpans(path);

        var response = await Post(path);

        (await response.Content.ReadAsStringAsync()).Should().Contain("Fault");
        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_cancellation_the_caller_did_not_ask_for_fails_the_request_and_its_span()
    {
        using var probe = ServerSpans("/cancelled");

        var response = await Post("/cancelled");

        (await response.Content.ReadAsStringAsync()).Should().Contain("Fault", "the caller gets a fault, not a bare 500");
        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task The_caller_going_away_is_a_stop_not_a_failure()
    {
        using var probe = ServerSpans("/slow");
        using var impatient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };
        var envelope = $"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>{Ping}</soap:Body></soap:Envelope>";

        var act = () => impatient.PostAsync($"http://127.0.0.1:{_port}/slow", new StringContent(envelope, Encoding.UTF8, "text/xml"));

        await act.Should().ThrowAsync<TaskCanceledException>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        probe.Activities.Should().ContainSingle().Which.Status.Should().NotBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task The_producer_opens_an_rpc_span_and_the_service_runs_under_it()
    {
        using var server = ServerSpans("/traced");
        using var client = new RouteTelemetryProbe(a => a.Kind == ActivityKind.Client
                                                        && RouteTelemetryProbe.Tag(a, "rpc.system") == "soap"
                                                        && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)!.Contains(_port.ToString(), StringComparison.Ordinal));
        var producer = _ctx.GetEndpoint($"direct://soap-trace-{_port}-call").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message(Ping)));

        var send = client.Activities.Should().ContainSingle().Subject;
        send.GetTagItem("rpc.method").Should().Be("Ping");
        send.GetTagItem("messaging.operation").Should().BeNull("an RPC call is not a messaging operation");
        send.GetTagItem("messaging.destination.name").Should().BeNull("the URL is not a messaging destination");
        server.Activities.Should().ContainSingle().Which.ParentSpanId.Should().Be(send.SpanId,
            "the request carries the context of the send");
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_offPort.ToString(), StringComparison.Ordinal) == true);
        var producer = _offCtx.GetEndpoint($"direct://soap-trace-{_offPort}-call").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message(Ping)));

        probe.Activities.Should().BeEmpty();
    }
}
