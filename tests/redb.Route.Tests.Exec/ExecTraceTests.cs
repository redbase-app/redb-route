using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Exec;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Exec;

/// <summary>
/// A scheduled tick carries no trace context: the consumer opens a root span per tick, never a child of the activity the
/// timer loop inherited from whoever started it, with the run of the command and the route under it; a failed route
/// marks it red. The producer's client span is named after the resolved executable, file name only, and carries it as
/// <c>process.executable.name</c>; a refused command marks it red. <c>EnableTelemetry=false</c> opens none of these spans.
/// </summary>
public sealed class ExecTraceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static bool IsWindows => OperatingSystem.IsWindows();
    private static string Shell => IsWindows ? "cmd" : "sh";
    private static string EchoArgs => IsWindows ? "/c%20echo%20tick" : "-c%20echo%20tick";

    private readonly string _routeId = $"exec-trace-{Guid.NewGuid():N}";

    private static string TickUri => $"exec://run?command={Shell}&args={EchoArgs}&schedule=200ms";

    private static RouteTelemetryProbe Ticks()
        => new(a => a.Kind == ActivityKind.Consumer && RouteTelemetryProbe.Tag(a, "process.executable.name") == Shell);

    private async Task<RouteContext> StartTicking(Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new ExecComponent());
        ctx.AddRoutes(r => r.From(TickUri).RouteId(_routeId).Process(step));
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
    public async Task Each_tick_opens_a_root_span_with_the_command_run_under_it()
    {
        using var ticks = Ticks();
        using var runs = new RouteTelemetryProbe(a => a.Kind == ActivityKind.Client
                                                      && RouteTelemetryProbe.Tag(a, "process.executable.name") == Shell);

        await using (await StartTicking(_ => { }))
            await Until(() => ticks.Activities.Count >= 2);

        ticks.Activities.Should().HaveCountGreaterThanOrEqualTo(2);
        ticks.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a tick carries no context, so its span starts a trace rather than joining the host's");
        var tick = ticks.Activities[0];
        runs.Activities.Should().Contain(a => a.ParentSpanId == tick.SpanId, "the command runs inside the tick");
    }

    [Fact]
    public async Task A_failed_route_marks_the_tick_span_red()
    {
        using var ticks = Ticks();
        IReadOnlyList<Activity> ended;

        await using (await StartTicking(_ => throw new InvalidOperationException("route failed")))
        {
            await Until(() => ticks.Activities.Count >= 1);
            ended = ticks.Activities;   // before the stop: a tick the stop interrupts is not a failure
        }

        ended.Should().NotBeEmpty().And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        // The tick spans and this route's spans; the producer tests of another class run commands of their own.
        using var probe = new RouteTelemetryProbe(a =>
            (a.Kind == ActivityKind.Consumer && RouteTelemetryProbe.Tag(a, "process.executable.name") == Shell)
            || RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.RouteIdTag) == _routeId);
        var ticked = new TaskCompletionSource();

        await using (await StartTicking(_ => ticked.TrySetResult(), telemetry: false))
            (await Task.WhenAny(ticked.Task, Task.Delay(Wait))).Should().Be(ticked.Task);

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task The_client_span_is_named_after_the_executable_file_and_a_refused_command_marks_it_red()
    {
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var runs = new RouteTelemetryProbe(a => a.Kind == ActivityKind.Client && a.TraceId == outer.TraceId);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new ExecComponent());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();
        var exchange = new Exchange(new Message(string.Empty));
        exchange.In.Headers[ExecHeaders.Command] = Path.Combine(Path.GetTempPath(), "secrets", "deploy-tool");

        var act = () => template.SendAsync("exec://run?allowedCommands=git", exchange);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        var run = runs.Activities.Should().ContainSingle().Subject;
        run.DisplayName.Should().Be("exec deploy-tool", "the file name only, not the path");
        run.GetTagItem("process.executable.name").Should().Be("deploy-tool");
        run.Status.Should().Be(ActivityStatusCode.Error);
    }
}
