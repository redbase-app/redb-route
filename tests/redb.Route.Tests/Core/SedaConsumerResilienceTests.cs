using FluentAssertions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Regression for GitHub issue #6: an unhandled exception in a SEDA consumer used to terminate the worker
/// loop permanently and silently (the loop caught only cancellation / channel-closed). One failing exchange
/// must not stop the consumer from draining the rest of the queue.
/// </summary>
public class SedaConsumerResilienceTests
{
    [Fact]
    public async Task Seda_keeps_consuming_after_one_exchange_fails()
    {
        await using var ctx = new RouteContext("test");
        var seen = new List<string>();

        ctx.AddRoutes(r => r.From("seda://work?concurrentConsumers=1")
            .RouteId("worker")
            .Process((ex, _) =>
            {
                var body = ex.In.Body?.ToString()!;
                if (body == "bad") throw new InvalidOperationException("boom");
                lock (seen) seen.Add(body);
                return Task.CompletedTask;
            }));

        await ctx.Start();
        using var producer = new ProducerTemplate(ctx);
        producer.Start();

        await producer.SendAsync("seda://work?concurrentConsumers=1", "first");
        await producer.SendAsync("seda://work?concurrentConsumers=1", "bad");
        await producer.SendAsync("seda://work?concurrentConsumers=1", "second");

        // The failing "bad" exchange must not stop the worker from reaching "second".
        await WaitUntil(() => { lock (seen) return seen.Count >= 2; }, TimeSpan.FromSeconds(5));

        lock (seen) seen.Should().Equal("first", "second");
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }
}
