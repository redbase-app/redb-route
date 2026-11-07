using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.SignalR;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// A hub message carries no headers: the consumer opens a root span per exchange that reached the route, linked to the
/// trace context of the connection request when the client sent one, never a child of the connection's activity or of
/// whoever started the routes. A failed route marks it red; the connection's own abort does not.
/// <c>EnableTelemetry=false</c> opens no span on either side.
/// </summary>
public sealed class SignalRTraceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly int _port = global::redb.Route.Tests.Shared.TestPorts.Next();

    private string HubUri => $"signalr://127.0.0.1:{_port}/traced";
    private string HubUrl => $"http://127.0.0.1:{_port}/traced";

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains($":{_port}", StringComparison.Ordinal) == true);

    private async Task<RouteContext> StartHub(Func<IExchange, CancellationToken, Task> step, bool telemetry = true, string query = "")
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new SignalRComponent());
        ctx.AddRoutes(r => r.From(HubUri + query).Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private async Task<HubConnection> Connect(string? traceparent = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(HubUrl, o =>
            {
                if (traceparent is not null)
                    o.Headers["traceparent"] = traceparent;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Each_exchange_opens_a_root_span_linked_to_the_handshake_context()
    {
        // The host traces its requests: the connection request's server span is the ambient activity of the hub.
        using var aspNet = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(aspNet);
        using var probe = Spans(ActivityKind.Consumer);
        var handshake = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);

        await using (await StartHub((_, _) => Task.CompletedTask))
        {
            await using var connection = await Connect($"00-{handshake.TraceId}-{handshake.SpanId}-01");
            await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "one" });
            await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "two" });
            await Until(() => probe.Activities.Count >= 3);
        }

        probe.Activities.Should().HaveCountGreaterThanOrEqualTo(3, "Connected and two invocations each reached the route");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a hub message carries no context; the hours-long connection is never its parent");
        probe.Activities.Should().OnlyContain(a => a.TraceId != handshake.TraceId);
        probe.Activities.Should().OnlyContain(a =>
            a.Links.Count() == 1 && a.Links.Single().Context.TraceId == handshake.TraceId && a.Links.Single().Context.SpanId == handshake.SpanId);
    }

    [Fact]
    public async Task A_connection_without_trace_headers_gives_spans_without_links()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartHub((_, _) => Task.CompletedTask))
        {
            await using var connection = await Connect();
            await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "one" });
            await Until(() => probe.Activities.Count >= 2);
        }

        probe.Activities.Should().HaveCountGreaterThanOrEqualTo(2);
        probe.Activities.Should().OnlyContain(a => !a.Links.Any() && a.ParentSpanId == default(ActivitySpanId));
    }

    [Fact]
    public async Task Nothing_arriving_opens_no_span()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartHub((_, _) => Task.CompletedTask))
            await Task.Delay(800);

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartHub((e, _) => e.In.Body is null
            ? Task.CompletedTask
            : throw new InvalidOperationException("route failed")))
        {
            await using var connection = await Connect();
            var invoke = () => connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "fail" });
            await invoke.Should().ThrowAsync<HubException>();
            await Until(() => probe.Activities.Count >= 2);
        }

        probe.Activities.Should().ContainSingle(a => a.Status == ActivityStatusCode.Error,
            "the invocation failed; the Connected event did not");
    }

    [Fact]
    public async Task The_connection_abort_cancelling_the_route_does_not_mark_the_span()
    {
        using var probe = Spans(ActivityKind.Consumer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (await StartHub(async (e, ct) =>
        {
            if (e.In.Body is null) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }))
        {
            var connection = await Connect();
            await connection.SendAsync("Invoke", "Send", new object?[] { "wait" });
            (await Task.WhenAny(entered.Task, Task.Delay(Wait))).Should().Be(entered.Task);
            await connection.DisposeAsync();
            await Until(() => probe.Activities.Count >= 3);
        }

        probe.Activities.Should().HaveCountGreaterThanOrEqualTo(3, "Connected, the cancelled invocation and Disconnected");
        probe.Activities.Should().OnlyContain(a => a.Status != ActivityStatusCode.Error,
            "cancellation by the connection's own abort is not a failure");
    }

    [Fact]
    public async Task A_failed_send_marks_the_producer_span_red()
    {
        using var probe = Spans(ActivityKind.Producer);

        await using var ctx = await StartHub((_, _) => throw new InvalidOperationException("route failed"), query: "?inOut=true");
        using var template = new ProducerTemplate(ctx);
        template.Start();
        var send = () => template.RequestAsync($"{HubUri}?bridge=true&inOut=true&method=Send", new Exchange(new Message("x")));
        await send.Should().ThrowAsync<Exception>();

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_on_either_side()
    {
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains($":{_port}", StringComparison.Ordinal) == true);

        await using (var ctx = await StartHub((_, _) => Task.CompletedTask, telemetry: false))
        {
            await using var connection = await Connect($"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01");
            await connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "off" });

            using var template = new ProducerTemplate(ctx);
            template.Start();
            await template.SendAsync($"{HubUri}?mode=Server&method=Send", new Exchange(new Message("off")));
        }

        probe.Activities.Should().BeEmpty();
    }
}
