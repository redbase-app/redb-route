using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Quartz;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// A scheduler fire carries no trace context: the consumer opens a root span per fire, never a child of the activity
/// the scheduler thread holds or the host started the routes under, with the route under it; a failed route marks it
/// red, our own stop does not. <c>EnableTelemetry=false</c> opens none. Each test schedules a job of its own.
/// </summary>
public sealed class QuartzTraceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _job = $"trace{Guid.NewGuid():N}";

    private RouteTelemetryProbe Fires()
        => new(a => a.Kind == ActivityKind.Consumer && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_job, StringComparison.Ordinal) == true);

    private async Task<RouteContext> StartTimer(Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new QuartzTimerComponent());
        ctx.AddRoutes(r => r.From($"qtimer://{_job}?period=100").RouteId(_job).Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Each_fire_opens_a_root_span_with_the_route_under_it()
    {
        using var fires = Fires();
        using var routes = RouteTelemetryProbe.ForRouteIdPrefix(_job);
        IReadOnlyList<Activity> ended;

        await using (await StartTimer(_ => { }))
        {
            await Until(() => fires.Activities.Count >= 2);
            ended = fires.Activities;
        }

        ended.Should().HaveCountGreaterThanOrEqualTo(2);
        ended.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a fire carries no context, so its span starts a trace rather than joining the host's");
        routes.Activities.Should().Contain(a => a.ParentSpanId == ended[0].SpanId, "the route runs inside the fire");
    }

    [Fact]
    public async Task A_failed_route_marks_the_fire_span_red()
    {
        using var fires = Fires();
        IReadOnlyList<Activity> ended;

        await using (await StartTimer(_ => throw new InvalidOperationException("route failed")))
        {
            await Until(() => fires.Activities.Count >= 1);
            ended = fires.Activities;   // before the stop: a fire the stop interrupts is not a failure
        }

        ended.Should().NotBeEmpty().And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_job, StringComparison.Ordinal) == true
            || RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.RouteIdTag) == _job);
        var fired = new TaskCompletionSource();

        await using (await StartTimer(_ => fired.TrySetResult(), telemetry: false))
            (await Task.WhenAny(fired.Task, Task.Delay(Wait))).Should().Be(fired.Task);

        probe.Activities.Should().BeEmpty();
    }
}
