using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Exec;

namespace redb.Route.Tests.Exec;

/// <summary>
/// A scheduled exec consumer keeps running the command after <c>Stop()</c> and <c>Start()</c> of the same route context:
/// the stop ends the schedule of the cached endpoint, the start runs it again.
/// </summary>
public sealed class ExecRestartTests
{
    private static bool IsWindows => OperatingSystem.IsWindows();
    private static string Shell => IsWindows ? "cmd" : "sh";
    private static string EchoArgs => IsWindows ? "/c%20echo%20tick" : "-c%20echo%20tick";

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        condition().Should().BeTrue(because);
    }

    [Fact]
    public async Task Schedule_runs_after_the_context_is_stopped_and_started_again()
    {
        var ticks = 0;
        await using var ctx = new RouteContext();
        ctx.AddComponent(new ExecComponent());
        ctx.AddRoutes(r => r.From($"exec://run?command={Shell}&args={EchoArgs}&schedule=200ms")
            .Process(_ => Interlocked.Increment(ref ticks)));

        await ctx.Start();
        await WaitForAsync(() => Volatile.Read(ref ticks) >= 1, "exec по расписанию запускает команду");

        await ctx.Stop();
        var before = Volatile.Read(ref ticks);
        await ctx.Start();
        await WaitForAsync(() => Volatile.Read(ref ticks) > before, "exec после рестарта снова запускает команду");
    }
}
