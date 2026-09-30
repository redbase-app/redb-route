using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Redis;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;
using StackExchange.Redis;

namespace redb.Route.Tests.Redis;

/// <summary>
/// A Redis value carries no trace context (a stream entry's fields are its body): the consumer opens a root span per
/// routed message, entry or item — pub/sub, stream and list alike — never a child of the activity the consumer loop
/// inherited from whoever started it, and none while nothing arrives. A failed route marks it red. The producer marks a
/// failed command red. <c>EnableTelemetry=false</c> opens none of these spans. Each test uses a key of its own and reads
/// only the spans of that endpoint. Requires Redis at localhost:6379.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisTraceTests
{
    private const string ConnectionString = "localhost:6379";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _key = $"trace-{Guid.NewGuid():N}";

    public static TheoryData<string> Modes => new() { "BLPOP", "XREAD", "SUBSCRIBE" };

    private string Uri(string operation) => $"redis:{operation}:{_key}?connectionString={ConnectionString}";

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_key, StringComparison.Ordinal) == true);

    private async Task Deliver(string mode, string value)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        switch (mode)
        {
            case "BLPOP":
                await redis.GetDatabase().ListRightPushAsync(_key, value);
                break;
            case "XREAD":
                await redis.GetDatabase().StreamAddAsync(_key, "v", value);
                break;
            default:
                await redis.GetSubscriber().PublishAsync(RedisChannel.Literal(_key), value);
                break;
        }
    }

    private async Task<RouteContext> StartConsumer(string mode, Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new RedisComponent());
        ctx.AddRoutes(r => r.From(Uri(mode)).Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        await Task.Delay(300);   // the subscription or the first blocking read is in place
        return ctx;
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Each_message_opens_a_root_span_even_under_an_ambient_activity(string mode)
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(mode, _ => { }))
        {
            await Deliver(mode, "a");
            await Deliver(mode, "b");
            await Until(() => probe.Activities.Count >= 2);
        }

        probe.Activities.Should().HaveCount(2, "one span per routed message");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a Redis value carries no context, so its span starts a trace rather than joining the host's");
        probe.Activities.Should().OnlyContain(a => RouteTelemetryProbe.Tag(a, "messaging.system") == "redis"
                                                   && a.GetTagItem("db.system") == null,
            "a received message is messaging, like its messaging.destination.name");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Nothing_arriving_opens_no_span(string mode)
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(mode, _ => { }))
            await Task.Delay(1200);

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_failed_route_marks_the_span_red(string mode)
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(mode, _ => throw new InvalidOperationException("route failed")))
        {
            await Deliver(mode, "fail");
            await Until(() => probe.Activities.Count >= 1);
        }

        probe.Activities.Should().NotBeEmpty().And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Tracing_off_opens_no_span(string mode)
    {
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_key, StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (await StartConsumer(mode, _ => routed.TrySetResult(), telemetry: false))
        {
            await Deliver(mode, "off");
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);
        }

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_command_marks_the_producer_span_red_and_tracing_off_opens_none(bool telemetry)
    {
        await using (var redis = await ConnectionMultiplexer.ConnectAsync(ConnectionString))
            await redis.GetDatabase().StringSetAsync(_key, "a string, not a list", TimeSpan.FromMinutes(1));
        using var probe = Spans(ActivityKind.Client);
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new RedisComponent());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var act = () => template.SendAsync(Uri("RPUSH"), new Exchange(new Message("item")));   // WRONGTYPE

        await act.Should().ThrowAsync<Exception>();
        if (telemetry)
            probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
        else
            probe.Activities.Should().BeEmpty();
    }
}
