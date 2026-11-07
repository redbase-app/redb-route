using System.Diagnostics;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Grpc;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// A gRPC call is inbound traffic like an HTTP request: the consumer's server span continues the caller's trace from
/// <c>traceparent</c> and puts the caller's baggage back, the route's spans sit under it, and a failed route marks it
/// red. A proxying producer sends the context of its own span, not the caller's header the bridge copied into the
/// metadata. <c>EnableTelemetry=false</c> opens none of these spans, while the caller's context still reaches the next
/// hop.
/// </summary>
public sealed class GrpcTracePropagationTests : IAsyncLifetime
{
    private const string Service = "trace.v1.Trace";
    private ServiceProvider _sp = null!;
    private RouteContext _ctx = null!;
    private RouteContext _offCtx = null!;
    private int _port;
    private int _offPort;

    public async Task InitializeAsync()
    {
        _port = global::redb.Route.Tests.Shared.TestPorts.Next();
        _offPort = global::redb.Route.Tests.Shared.TestPorts.Next();

        var services = new ServiceCollection();
        services.AddRedbRouteGrpc();
        _sp = services.BuildServiceProvider();
        var servers = _sp.GetRequiredService<redb.Route.Http.SharedHttpServerManager>();

        var port = _port;
        _ctx = new RouteContext(_sp, $"grpc-trace-{port}");
        _ctx.AddComponent(new GrpcComponent { ServerManager = servers });
        _ctx.AddRoutes(r =>
        {
            r.From(GrpcDsl.Listen($"127.0.0.1:{port}").Method($"/{Service}/Traced")).RouteId($"grpc-trace-{port}-traced")
                .Process(e => e.Out = new Message("ok"));
            r.From(GrpcDsl.Listen($"127.0.0.1:{port}").Method($"/{Service}/Fails")).RouteId($"grpc-trace-{port}-fails")
                .Process(_ => throw new InvalidOperationException("route failed"));
            r.From(GrpcDsl.Listen($"127.0.0.1:{port}").Method($"/{Service}/Baggage")).RouteId($"grpc-trace-{port}-baggage")
                .Process(e => e.Out = new Message(Activity.Current?.GetBaggageItem("tenant") ?? "<none>"));
            r.From(GrpcDsl.Listen($"127.0.0.1:{port}").Method($"/{Service}/Proxy")).RouteId($"grpc-trace-{port}-proxy")
                .To(GrpcDsl.Call($"127.0.0.1:{port}").Method($"/{Service}/Echo"));
            r.From(GrpcDsl.Listen($"127.0.0.1:{port}").Method($"/{Service}/Cancelled")).RouteId($"grpc-trace-{port}-cancelled")
                .Process(_ => throw new TaskCanceledException("a call inside the route timed out"));
            r.From(GrpcDsl.Listen($"127.0.0.1:{port}").Method($"/{Service}/Slow")).RouteId($"grpc-trace-{port}-slow")
                .Process(async (_, ct) => await Task.Delay(5000, ct));
            r.From(GrpcDsl.Listen($"127.0.0.1:{port}").Method($"/{Service}/Echo")).RouteId($"grpc-trace-{port}-echo")
                .Process(e => e.Out = new Message(e.In.Headers.TryGetValue("traceparent", out var tp) ? tp?.ToString() : "<none>"));
        });
        await _ctx.Start();

        var offPort = _offPort;
        _offCtx = new RouteContext(_sp, $"grpc-trace-off-{offPort}", options: new RouteEngineOptions { EnableTelemetry = false });
        _offCtx.AddComponent(new GrpcComponent { ServerManager = servers });
        _offCtx.AddRoutes(r =>
        {
            r.From(GrpcDsl.Listen($"127.0.0.1:{offPort}").Method($"/{Service}/Proxy"))
                .To(GrpcDsl.Call($"127.0.0.1:{offPort}").Method($"/{Service}/Echo"));
            r.From(GrpcDsl.Listen($"127.0.0.1:{offPort}").Method($"/{Service}/Echo"))
                .Process(e => e.Out = new Message(e.In.Headers.TryGetValue("traceparent", out var tp) ? tp?.ToString() : "<none>"));
        });
        await _offCtx.Start();
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        await _offCtx.DisposeAsync();
        await _sp.DisposeAsync();
    }

    private static (ActivityTraceId TraceId, ActivitySpanId SpanId, string Header) Caller()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return (traceId, spanId, $"00-{traceId}-{spanId}-01");
    }

    /// <summary>Calls <paramref name="method"/> with <paramref name="headers"/> as metadata, outside any activity.</summary>
    private static async Task<string> Call(int port, string method, params (string Name, string Value)[] headers)
    {
        var ambient = Activity.Current;
        Activity.Current = null;   // the metadata alone carries the caller's context
        try
        {
            using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
            var marshaller = Marshallers.Create<byte[]>(b => b, b => b);
            var call = new Method<byte[], byte[]>(MethodType.Unary, Service, method, marshaller, marshaller);
            var metadata = new Metadata();
            foreach (var (name, value) in headers)
                metadata.Add(name, value);
            var reply = await channel.CreateCallInvoker()
                .AsyncUnaryCall(call, null, new CallOptions(metadata), Array.Empty<byte>());
            return Encoding.UTF8.GetString(reply);
        }
        finally
        {
            Activity.Current = ambient;
        }
    }

    private static Activity ServerSpan(RouteTelemetryProbe probe, string method)
        => probe.Activities.Single(a => a.Kind == ActivityKind.Server
                                         && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)!.Contains(method, StringComparison.Ordinal));

    [Fact]
    public async Task A_call_opens_a_server_span_that_continues_the_callers_trace()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"{_port}/{Service}/Traced");
        using var routes = RouteTelemetryProbe.ForRouteIdPrefix($"grpc-trace-{_port}-traced");
        var caller = Caller();

        (await Call(_port, "Traced", ("traceparent", caller.Header))).Should().Be("ok");

        var server = ServerSpan(probe, "/Traced");
        server.TraceId.Should().Be(caller.TraceId);
        server.ParentSpanId.Should().Be(caller.SpanId);
        server.GetTagItem("rpc.system").Should().Be("grpc");
        routes.Activities.Should().ContainSingle().Which.ParentSpanId.Should().Be(server.SpanId,
            "the route runs inside the call");
    }

    [Fact]
    public async Task A_call_without_context_opens_a_root_server_span()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"{_port}/{Service}/Traced");

        await Call(_port, "Traced");

        ServerSpan(probe, "/Traced").ParentSpanId.Should().Be(default(ActivitySpanId));
    }

    [Fact]
    public async Task The_callers_baggage_reaches_the_route()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"{_port}/{Service}/Baggage");
        var headers = new Dictionary<string, string>();
        using (var send = new Activity("caller").Start())
        {
            send.AddBaggage("tenant", "t1");
            RouteTelemetryExtensions.InjectTraceContext(send, headers, static (h, name, value) => h[name] = value);
        }

        (await Call(_port, "Baggage", headers.Select(h => (h.Key, h.Value)).ToArray())).Should().Be("t1");
    }

    [Fact]
    public async Task A_failed_route_marks_the_server_span_red()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"{_port}/{Service}/Fails");

        var act = () => Call(_port, "Fails");

        await act.Should().ThrowAsync<RpcException>();
        ServerSpan(probe, "/Fails").Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_timeout_inside_the_route_marks_the_server_span_red()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"{_port}/{Service}/Cancelled");

        var act = () => Call(_port, "Cancelled");

        await act.Should().ThrowAsync<RpcException>();
        ServerSpan(probe, "/Cancelled").Status.Should().Be(ActivityStatusCode.Error);
    }

    /// <summary>Calls the slow method with <paramref name="options"/>, and waits for its server span.</summary>
    private async Task<Activity> SlowCallSpan(CallOptions options)
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"{_port}/{Service}/Slow");
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_port}");
        var marshaller = Marshallers.Create<byte[]>(b => b, b => b);
        var method = new Method<byte[], byte[]>(MethodType.Unary, Service, "Slow", marshaller, marshaller);

        var act = () => channel.CreateCallInvoker().AsyncUnaryCall(method, null, options, Array.Empty<byte>()).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        return probe.Activities.Should().ContainSingle().Subject;
    }

    [Fact]
    public async Task The_caller_going_away_is_a_stop_not_a_failure()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var server = await SlowCallSpan(new CallOptions(cancellationToken: cancel.Token));

        server.Status.Should().NotBe(ActivityStatusCode.Error, "the caller cancelled the call it made");
    }

    [Fact]
    public async Task A_proxy_sends_the_context_of_its_own_span_not_the_copied_caller_header()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($"{_port}/{Service}/Echo");
        var caller = Caller();

        var received = await Call(_port, "Proxy", ("traceparent", caller.Header));

        var send = probe.Activities.Single(a => a.Kind == ActivityKind.Client);
        received.Should().Be($"00-{caller.TraceId}-{send.SpanId}-01",
            "the next hop is a child of the send, in the caller's trace");
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_and_still_passes_the_callers_context_on()
    {
        using var probe = RouteTelemetryProbe.ForEndpointContaining($":{_offPort}/");
        var caller = Caller();

        (await Call(_offPort, "Proxy", ("traceparent", caller.Header))).Should().Contain(caller.TraceId.ToString());
        probe.Activities.Should().BeEmpty();
    }
}
