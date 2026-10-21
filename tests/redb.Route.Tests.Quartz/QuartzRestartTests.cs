using redb.Route.Core;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// Quartz timers keep firing after <c>Stop()</c> and <c>Start()</c> of the same route context: the stop removes the
/// jobs of the cached endpoints from the scheduler, the start schedules them again.
/// </summary>
public class QuartzRestartTests
{
    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        condition().Should().BeTrue(because);
    }

    [Fact]
    public async Task Qtimer_fires_after_the_context_is_stopped_and_started_again()
    {
        var fired = 0;
        await using var context = new RouteContext();
        context.AddComponent(new QuartzTimerComponent());
        context.AddRoutes(r => r.From("qtimer://restart?period=100").Process(_ => Interlocked.Increment(ref fired)));

        await context.Start();
        await WaitForAsync(() => Volatile.Read(ref fired) >= 1, "qtimer срабатывает");

        await context.Stop();
        var before = Volatile.Read(ref fired);
        await context.Start();
        await WaitForAsync(() => Volatile.Read(ref fired) > before, "qtimer после рестарта снова срабатывает");
    }

    [Fact]
    public async Task Cron_fires_after_the_context_is_stopped_and_started_again()
    {
        var fired = 0;
        await using var context = new RouteContext();
        context.AddComponent(new CronComponent());
        context.AddRoutes(r => r.From("cron://restart?schedule=0/1 * * * * ?").Process(_ => Interlocked.Increment(ref fired)));

        await context.Start();
        await WaitForAsync(() => Volatile.Read(ref fired) >= 1, "cron срабатывает");

        await context.Stop();
        var before = Volatile.Read(ref fired);
        await context.Start();
        await WaitForAsync(() => Volatile.Read(ref fired) > before, "cron после рестарта снова срабатывает");
    }
}
