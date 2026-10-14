using System.Collections.Specialized;
using Quartz;
using Quartz.Impl;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// How a Quartz consumer claims its job and when the scheduler starts, on the in-memory store. One job key, one
/// consumer per scheduler: a second claimant — the same path with another schedule, another component with the same
/// path, another context on the same scheduler — fails at its start instead of silently replacing the first schedule.
/// The scheduler, the context's own or one handed to it, starts once the context has started (camel-quartz
/// <c>autoStartScheduler</c>), so a fire never finds a consumer not yet registered.
/// </summary>
public class QuartzSchedulingTests
{
    [Fact]
    public async Task SamePath_TwoSchedules_InOneContext_TheSecondFails_TheFirstKeepsItsSchedule()
    {
        await using var ctx = new RouteContext();
        ctx.AddComponent(new CronComponent());
        ctx.AddRoutes(r =>
        {
            r.From("cron://key-nightly?schedule=0 0 3 * * ?").RouteId("report-3am").Process(_ => { });
            r.From("cron://key-nightly?schedule=0 0 4 * * ?").RouteId("report-4am").Process(_ => { });
        });

        await ctx.Start();

        var started = ctx.Routes.Where(r => r.Status == RouteStatus.Started).Select(r => r.RouteId).ToList();
        started.Should().ContainSingle("one job key, one schedule — the second claimant does not start");
        var scheduler = ctx.GetService<IScheduler>()!;
        var trigger = (ICronTrigger)(await scheduler.GetTrigger(new TriggerKey("key-nightly", "redb")))!;
        trigger.CronExpressionString.Should().Be(started[0] == "report-3am" ? "0 0 3 * * ?" : "0 0 4 * * ?",
            "the schedule of the route that started is not replaced by the one that did not");
    }

    [Fact]
    public async Task SamePath_CronAndQtimer_InOneContext_TheSecondFails()
    {
        await using var ctx = new RouteContext();
        ctx.AddComponent(new CronComponent());
        ctx.AddComponent(new QuartzTimerComponent());
        ctx.AddRoutes(r =>
        {
            r.From("cron://key-shared?schedule=0 0 3 * * ?").Process(_ => { });
            r.From("qtimer://key-shared?period=60000").Process(_ => { });
        });

        await ctx.Start();

        ctx.Routes.Count(r => r.Status == RouteStatus.Started).Should().Be(1);
    }

    [Fact]
    public async Task SameUri_TwoContexts_OnOneScheduler_TheSecondFails()
    {
        var scheduler = await NewScheduler();
        try
        {
            await using var first = new RouteContext(contextId: "key-ctx-a");
            first.AddService(typeof(IScheduler), scheduler);
            first.AddComponent(new CronComponent());
            first.AddRoutes(r => r.From("cron://key-module?schedule=0 0 3 * * ?").Process(_ => { }));
            await first.Start();

            await using var second = new RouteContext(contextId: "key-ctx-b");
            second.AddService(typeof(IScheduler), scheduler);
            second.AddComponent(new CronComponent());
            second.AddRoutes(r => r.From("cron://key-module?schedule=0 0 3 * * ?").Process(_ => { }));
            await second.Start();

            first.Routes.Single().Status.Should().Be(RouteStatus.Started);
            second.Routes.Single().Status.Should().NotBe(RouteStatus.Started,
                "two contexts of one process cannot share one job of one scheduler");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task AHandedInScheduler_StartsOnlyOnceTheContextHasStarted()
    {
        var scheduler = await NewScheduler();
        try
        {
            var probe = new StartedAtRouteStart(scheduler);
            await using var ctx = new RouteContext();
            ctx.AddService(typeof(IScheduler), scheduler);
            ctx.AddComponent(new QuartzTimerComponent());
            ctx.AddLifecycleListener(probe);
            ctx.AddRoutes(r => r.From("qtimer://start-order?period=60000").Process(_ => { }));

            await ctx.Start();

            probe.SchedulerStartedWhenTheRouteStarted.Should().BeFalse(
                "a consumer registers; the scheduler starts after the whole context has started");
            scheduler.IsStarted.Should().BeTrue("auto-start, as camel-quartz autoStartScheduler");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task ARouteStartedLater_StartsTheSchedulerItself()
    {
        var scheduler = await NewScheduler();
        try
        {
            await using var ctx = new RouteContext();
            ctx.AddService(typeof(IScheduler), scheduler);
            ctx.AddComponent(new QuartzTimerComponent());
            ctx.AddRoutes(r => r.From("qtimer://started-later?period=60000").RouteId("started-later").AutoStart(false)
                .Process(_ => { }));
            await ctx.Start();
            scheduler.IsStarted.Should().BeFalse("no Quartz consumer started with the context");

            await ctx.StartRoute("started-later");

            scheduler.IsStarted.Should().BeTrue("the context is already running");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    private static Task<IScheduler> NewScheduler()
        => new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "redb-route-test-" + Guid.NewGuid().ToString("N"),
        }).GetScheduler();

    private sealed class StartedAtRouteStart(IScheduler scheduler) : IRouteLifecycleListener
    {
        public bool? SchedulerStartedWhenTheRouteStarted { get; private set; }

        public Task OnRouteStarted(string routeId, CancellationToken ct)
        {
            SchedulerStartedWhenTheRouteStarted ??= scheduler.IsStarted;
            return Task.CompletedTask;
        }
    }
}
