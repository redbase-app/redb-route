using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary>One computation per key at a time, across every cache node of the process that shares the store.</summary>
public class SingleFlightTests
{
    [Fact]
    public async Task ConcurrentMisses_OnOneKey_AcrossTwoNodes_RunTheInnerStepsOnce()
    {
        var calls = 0;
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            foreach (var name in new[] { "a", "b" })
                b.From($"direct://flight-{name}")
                    .Cache("hot", TimeSpan.FromMinutes(1)).Region("flight")
                        .Process(async (e, ct) =>
                        {
                            Interlocked.Increment(ref calls);
                            await Task.Delay(300, ct);
                            e.In.Body = "computed";
                        })
                    .EndCache()
                    .To("mock://flight");
        });
        await ctx.Start();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => ctx.SendBody(i % 2 == 0 ? "direct://flight-a" : "direct://flight-b", "in")));

        calls.Should().Be(1, "the two nodes share one store, so they share one flight per key");
        ctx.Mock("mock://flight").ReceivedExchanges.Select(e => e.In.Body).Should().HaveCount(20).And.AllBeEquivalentTo("computed");
    }
}
