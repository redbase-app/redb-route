using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Tcp;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// A framed TCP message carries no trace context: the consumer opens a root span per routed message, never a child of the
/// activity the listener inherited from whoever started it, and none while a connection sends nothing. A failed route
/// marks it red. <c>EnableTelemetry=false</c> opens no span, on the consumer or the producer.
/// </summary>
public sealed class TcpTraceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly int _port = global::redb.Route.Tests.Shared.TestPorts.Next();

    private string Uri => $"tcp://127.0.0.1:{_port}?framing=TextLine";

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains($":{_port}", StringComparison.Ordinal) == true);

    private async Task<RouteContext> StartConsumer(Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new TcpComponent());
        ctx.AddRoutes(r => r.From(Uri).Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private async Task<TcpClient> Send(params string[] lines)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        foreach (var line in lines)
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await client.GetStream().FlushAsync();
        return client;
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Each_message_opens_a_root_span_even_under_an_ambient_activity()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => { }))
        {
            using var client = await Send("a", "b");
            await Until(() => probe.Activities.Count >= 2);
        }

        probe.Activities.Should().HaveCount(2, "one span per routed message");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a framed message carries no context, so its span starts a trace rather than joining the host's");
    }

    [Fact]
    public async Task A_connection_that_sends_nothing_opens_no_span()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => { }))
        {
            using var client = await Send();
            await Task.Delay(800);
        }

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => throw new InvalidOperationException("route failed")))
        {
            using var client = await Send("fail");
            await Until(() => probe.Activities.Count >= 1);
        }

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_on_either_side()
    {
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains($":{_port}", StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (var ctx = await StartConsumer(_ => routed.TrySetResult(), telemetry: false))
        {
            using var template = new ProducerTemplate(ctx);
            template.Start();
            await template.SendAsync(Uri, new Exchange(new Message("off")));
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);
        }

        probe.Activities.Should().BeEmpty();
    }
}
