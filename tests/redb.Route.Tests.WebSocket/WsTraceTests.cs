using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Tests.Telemetry;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// A WebSocket frame carries no headers: the consumer opens a root span per routed message, linked to the trace context
/// of the upgrade request when the client sent one, never a child of the connection's activity or of whoever started
/// the routes. A failed route marks it red; our own stop does not. <c>EnableTelemetry=false</c> opens no span on either
/// side.
/// </summary>
public sealed class WsTraceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly int _port = global::redb.Route.Tests.Shared.TestPorts.Next();

    private string Uri => $"ws://127.0.0.1:{_port}/traced";

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains($":{_port}", StringComparison.Ordinal) == true);

    private async Task<RouteContext> StartServer(Func<IExchange, CancellationToken, Task> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new WsComponent());
        ctx.AddRoutes(r => r.From(Uri).Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private async Task<ClientWebSocket> Connect(string? traceparent = null)
    {
        var client = new ClientWebSocket();
        if (traceparent is not null)
            client.Options.SetRequestHeader("traceparent", traceparent);
        await client.ConnectAsync(new System.Uri(Uri), CancellationToken.None);
        return client;
    }

    private static Task Send(ClientWebSocket client, string text)
        => client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Each_message_opens_a_root_span_linked_to_the_handshake_context()
    {
        // The host traces its requests: the upgrade request's server span is the ambient activity of the socket loop.
        using var aspNet = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(aspNet);
        using var probe = Spans(ActivityKind.Consumer);
        var handshake = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);

        await using (await StartServer((_, _) => Task.CompletedTask))
        {
            using var client = await Connect($"00-{handshake.TraceId}-{handshake.SpanId}-01");
            await Send(client, "a");
            await Send(client, "b");
            await Until(() => probe.Activities.Count >= 2);
        }

        probe.Activities.Should().HaveCount(2, "one span per routed message");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a frame carries no context; the long-lived connection is never its parent");
        probe.Activities.Should().OnlyContain(a => a.TraceId != handshake.TraceId);
        probe.Activities.Should().OnlyContain(a =>
            a.Links.Count() == 1 && a.Links.Single().Context.TraceId == handshake.TraceId && a.Links.Single().Context.SpanId == handshake.SpanId);
    }

    [Fact]
    public async Task A_connection_without_trace_headers_gives_spans_without_links()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartServer((_, _) => Task.CompletedTask))
        {
            using var client = await Connect();
            await Send(client, "a");
            await Until(() => probe.Activities.Count >= 1);
        }

        probe.Activities.Should().ContainSingle().Which.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_that_sends_nothing_opens_no_span()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartServer((_, _) => Task.CompletedTask))
        {
            using var client = await Connect($"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01");
            await Task.Delay(800);
        }

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartServer((_, _) => throw new InvalidOperationException("route failed")))
        {
            using var client = await Connect();
            await Send(client, "fail");
            await Until(() => probe.Activities.Count >= 1);
        }

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Our_stop_cancelling_the_route_does_not_mark_the_span()
    {
        using var probe = Spans(ActivityKind.Consumer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>());
            });
        var component = new WsComponent();
        var endpoint = (WsEndpoint)component.CreateEndpoint(
            new EndpointUri("ws", $"/127.0.0.1:{_port}/traced", $"ws:127.0.0.1:{_port}/traced", new Dictionary<string, string>()));
        var consumer = new WsConsumer(endpoint, processor, endpoint.EndpointOptions);
        using var stop = new CancellationTokenSource();
        await consumer.Start(stop.Token);
        try
        {
            using var client = await Connect();
            await Send(client, "wait");
            (await Task.WhenAny(entered.Task, Task.Delay(Wait))).Should().Be(entered.Task);

            // Our stop from above: the token the consumer was started with.
            await stop.CancelAsync();
            await Until(() => probe.Activities.Count >= 1);
        }
        finally
        {
            await consumer.Stop();
        }

        probe.Activities.Should().ContainSingle().Which.Status.Should().NotBe(ActivityStatusCode.Error,
            "our own stop cancelling the route is not a failure");
    }

    [Fact]
    public async Task A_failed_send_marks_the_producer_span_red()
    {
        using var probe = Spans(ActivityKind.Producer);

        await using (var ctx = await StartServer((_, _) => Task.CompletedTask))
        {
            using var template = new ProducerTemplate(ctx);
            template.Start();
            await template.SendAsync($"{Uri}?mode=Server", new Exchange(new Message("broadcast")));

            // A push to a connection that is not there fails.
            var gone = new Message("gone");
            gone.Headers[WsHeaders.TargetConnection] = "no-such-connection";
            var send = () => template.SendAsync($"{Uri}?mode=Server", new Exchange(gone));
            await send.Should().ThrowAsync<InvalidOperationException>();
        }

        probe.Activities.Should().HaveCount(2);
        probe.Activities[0].Status.Should().NotBe(ActivityStatusCode.Error);
        probe.Activities[1].Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_on_either_side()
    {
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains($":{_port}", StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (var ctx = await StartServer((_, _) => { routed.TrySetResult(); return Task.CompletedTask; }, telemetry: false))
        {
            using var client = await Connect($"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01");
            await Send(client, "off");
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);

            using var template = new ProducerTemplate(ctx);
            template.Start();
            await template.SendAsync($"{Uri}?mode=Server", new Exchange(new Message("off")));
        }

        probe.Activities.Should().BeEmpty();
    }
}
