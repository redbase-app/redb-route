using System.Diagnostics;
using System.Net;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Http;

/// <summary>
/// An HTTP request is inbound traffic like a broker message: the consumer opens a server span with the endpoint on it,
/// continues the caller's trace from <c>traceparent</c> and puts the caller's baggage back; the route's own spans sit
/// under it; a proxying producer sends the context of its own span on, not the caller's header it copied; a failed
/// route marks the server span red; and <c>EnableTelemetry=false</c> opens none of these spans, while the caller's
/// context still reaches the next hop.
/// </summary>
[Collection("HttpServer")]
public sealed class HttpTracePropagationTests : IAsyncLifetime
{
    private int _port;
    private int _offPort;
    private HttpClient _client = null!;
    private RouteContext _ctx = null!;
    private RouteContext _offCtx = null!;
    private SharedHttpServerManager _servers = null!;

    public async Task InitializeAsync()
    {
        _port = global::redb.Route.Tests.Shared.TestPorts.Next();
        _offPort = global::redb.Route.Tests.Shared.TestPorts.Next();
        _client = new HttpClient();
        _servers = new SharedHttpServerManager();

        var port = _port;
        _ctx = new RouteContext();
        _ctx.AddComponent(new HttpComponent { ServerManager = _servers });
        _ctx.AddRoutes(r =>
        {
            r.From($"http://127.0.0.1:{port}/traced?inOut=true").RouteId($"trace-{port}-traced").SetBody("ok");
            r.From($"http://127.0.0.1:{port}/fails?inOut=true").RouteId($"trace-{port}-fails")
                .Process(_ => throw new InvalidOperationException("route failed"));
            r.From($"http://127.0.0.1:{port}/baggage?inOut=true").RouteId($"trace-{port}-baggage")
                .Process(e => e.In.Body = Activity.Current?.GetBaggageItem("tenant") ?? "<none>");
            r.From($"http://127.0.0.1:{port}/proxy?inOut=true").RouteId($"trace-{port}-proxy")
                .To($"http://127.0.0.1:{port}/echo");
            r.From($"http://127.0.0.1:{port}/cancelled?inOut=true").RouteId($"trace-{port}-cancelled")
                .Process(_ => throw new TaskCanceledException("a call inside the route timed out"));
            r.From($"http://127.0.0.1:{port}/timeout?inOut=true").RouteId($"trace-{port}-timeout")
                .To($"http://127.0.0.1:{port}/slow?timeout=200");
            r.From($"http://127.0.0.1:{port}/slow?inOut=true").RouteId($"trace-{port}-slow")
                .Process(async (_, ct) => await Task.Delay(3000, ct));
            r.From($"http://127.0.0.1:{port}/echo?inOut=true").RouteId($"trace-{port}-echo")
                .Process(e => e.In.Body = e.In.Headers.TryGetValue("traceparent", out var tp) ? tp?.ToString() : "<none>");
        });
        await _ctx.Start();

        var offPort = _offPort;
        _offCtx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = false });
        _offCtx.AddComponent(new HttpComponent { ServerManager = _servers });
        _offCtx.AddRoutes(r =>
        {
            r.From($"http://127.0.0.1:{offPort}/proxy?inOut=true").To($"http://127.0.0.1:{offPort}/echo");
            r.From($"http://127.0.0.1:{offPort}/echo?inOut=true")
                .Process(e => e.In.Body = e.In.Headers.TryGetValue("traceparent", out var tp) ? tp?.ToString() : "<none>");
        });
        await _offCtx.Start();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _ctx.DisposeAsync();
        await _offCtx.DisposeAsync();
        await _servers.DisposeAsync();
    }

    private static (ActivityTraceId TraceId, ActivitySpanId SpanId, string Header) Caller()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return (traceId, spanId, $"00-{traceId}-{spanId}-01");
    }

    private async Task<HttpResponseMessage> Get(int port, string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return await _client.SendAsync(request);
    }

    private static Activity ServerSpan(RouteTelemetryProbe probe, string path)
        => probe.Activities.Single(a => a.Kind == ActivityKind.Server
                                         && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)!.Contains(path, StringComparison.Ordinal));

    [Fact]
    public async Task A_request_opens_a_server_span_that_continues_the_callers_trace()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{_port}/traced");
        using var routes = RouteTelemetryProbe.ForRouteIdPrefix($"trace-{_port}-traced");
        var caller = Caller();

        var response = await Get(_port, "/traced", ("traceparent", caller.Header));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var server = ServerSpan(probe, "/traced");
        server.TraceId.Should().Be(caller.TraceId);
        server.ParentSpanId.Should().Be(caller.SpanId);
        server.Source.Name.Should().Be(RouteActivitySource.SourceName);
        server.GetTagItem("http.response.status_code").Should().Be(200);

        var route = routes.Activities.Should().ContainSingle().Subject;
        route.ParentSpanId.Should().Be(server.SpanId, "the route runs inside the request");
    }

    [Fact]
    public async Task The_callers_baggage_reaches_the_route()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{_port}/baggage");
        var headers = new Dictionary<string, string>();
        using (var send = new Activity("caller").Start())
        {
            send.AddBaggage("tenant", "t1");
            RouteTelemetryExtensions.InjectTraceContext(send, headers,
                static (h, name, value) => h[name] = value);
        }

        var response = await Get(_port, "/baggage", headers.Select(h => (h.Key, h.Value)).ToArray());

        (await response.Content.ReadAsStringAsync()).Should().Be("t1");
    }

    [Fact]
    public async Task A_failed_route_marks_the_server_span_red()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{_port}/fails");

        var response = await Get(_port, "/fails");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var server = ServerSpan(probe, "/fails");
        server.Status.Should().Be(ActivityStatusCode.Error);
        server.GetTagItem("http.response.status_code").Should().Be(500);
    }

    [Fact]
    public async Task A_call_timed_out_by_HttpClient_marks_the_send_span_red()
    {
        // HttpClient reports its own timeout as a TaskCanceledException, an OperationCanceledException: only a
        // cancellation by the caller's token is a stop rather than a failure.
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{_port}/slow");

        var response = await Get(_port, "/timeout");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var send = probe.Activities.Single(a => a.Kind == ActivityKind.Client);
        send.Status.Should().Be(ActivityStatusCode.Error);
        send.Events.Should().Contain(e => e.Name == "exception");
    }

    [Fact]
    public async Task A_cancellation_the_caller_did_not_ask_for_fails_the_request_and_its_span()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{_port}/cancelled");

        var response = await Get(_port, "/cancelled");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        ServerSpan(probe, "/cancelled").Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_proxy_sends_the_context_of_its_own_span_not_the_copied_caller_header()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{_port}/echo");
        var caller = Caller();

        var response = await Get(_port, "/proxy", ("traceparent", caller.Header));

        var received = await response.Content.ReadAsStringAsync();
        var send = probe.Activities.Single(a => a.Kind == ActivityKind.Client);
        received.Should().Be($"00-{caller.TraceId}-{send.SpanId}-01",
            "the next hop is a child of the send, in the caller's trace");
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_and_still_passes_the_callers_context_on()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{_offPort}/");
        var caller = Caller();

        var response = await Get(_offPort, "/proxy", ("traceparent", caller.Header));

        (await response.Content.ReadAsStringAsync()).Should().Contain(caller.TraceId.ToString());
        probe.Activities.Should().BeEmpty();
    }
}
