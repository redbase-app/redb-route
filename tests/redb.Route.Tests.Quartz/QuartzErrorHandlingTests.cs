using redb.Route.Core;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// A failed fire goes through the route's own error handling once — inside the pipeline, as every other consumer —
/// and the consumer only records the outcome. It used to hand an unhandled failure to
/// <c>IRouteContext.HandleException</c> as well, so an <c>OnException</c> without <c>Handled()</c> ran twice and a
/// route-level one ended in "No exception handler found".
/// </summary>
public class QuartzErrorHandlingTests
{
    [Fact]
    public async Task UnhandledFailure_RunsTheBuildersOnExceptionOnce()
    {
        var hits = 0;
        await using var context = new RouteContext();
        context.AddComponent(new QuartzTimerComponent());
        context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>().Process(_ => Interlocked.Increment(ref hits));
            r.From("qtimer://builder-once?period=50&repeatCount=0")
                .Process(_ => throw new InvalidOperationException("boom"));
        });

        await context.Start();
        await WaitFor(() => Volatile.Read(ref hits) >= 1);
        await Task.Delay(300);

        Volatile.Read(ref hits).Should().Be(1, "the handler runs inside the route, the consumer does not run it again");
    }

    [Fact]
    public async Task UnhandledFailure_RunsTheRoutesOnExceptionOnce()
    {
        var hits = 0;
        await using var context = new RouteContext();
        context.AddComponent(new QuartzTimerComponent());
        context.AddRoutes(r => r
            .From("qtimer://route-once?period=50&repeatCount=0")
            .OnException<InvalidOperationException>()
                .Process(_ => Interlocked.Increment(ref hits))
            .End()
            .Process(_ => throw new InvalidOperationException("boom")));

        await context.Start();
        await WaitFor(() => Volatile.Read(ref hits) >= 1);
        await Task.Delay(300);

        Volatile.Read(ref hits).Should().Be(1);
    }

    [Fact]
    public async Task UnhandledFailure_ReachesTheContextErrorHandlerOnce()
    {
        // The dead letter queue of a host (Tsak's checkpoint DLQ) is the context's ErrorHandler: a failed fire no
        // OnException took reaches it exactly once.
        var calls = 0;
        await using var context = new RouteContext { ErrorHandler = new CountingErrorHandler(() => Interlocked.Increment(ref calls)) };
        context.AddComponent(new QuartzTimerComponent());
        context.AddRoutes(r => r
            .From("qtimer://errorhandler-once?period=50&repeatCount=0")
            .Process(_ => throw new InvalidOperationException("boom")));

        await context.Start();
        await WaitFor(() => Volatile.Read(ref calls) >= 1);
        await Task.Delay(300);

        Volatile.Read(ref calls).Should().Be(1);
    }

    private sealed class CountingErrorHandler(Action onCall) : redb.Route.Abstractions.IErrorHandler
    {
        public Task HandleError(redb.Route.Abstractions.IExchange exchange, Exception exception, CancellationToken ct = default)
        {
            onCall();
            return Task.CompletedTask;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}
