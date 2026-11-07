using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Core;

/// <summary>
/// Code review 2026-09-01: the listener list is enumerated per exchange, so registering a listener
/// while exchanges flow (NotifyBuilder on a running context, a listener adding another from a
/// callback) must never race that enumeration. Before, a plain List threw "Collection was modified"
/// out of the notification and failed an unrelated in-flight message.
/// </summary>
public class LifecycleListenerReviewTests
{
    [Fact]
    public async Task AListenerMayRegisterAnotherListener_FromAnExchangeCallback()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://listen").To("mock://listen"));
        var added = 0;
        ctx.AddLifecycleListener(new RegisteringListener(() =>
        {
            ctx.AddLifecycleListener(new Silent());
            added++;
        }));
        await ctx.Start();

        await ctx.SendBody("direct://listen", "x");
        await ctx.SendBody("direct://listen", "y");

        added.Should().Be(2);
        await ctx.Mock("mock://listen").ExpectMessageCount(2).AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class RegisteringListener(Action onReceived) : IRouteLifecycleListener
    {
        public Task OnExchangeReceived(string routeId, IExchange exchange, CancellationToken ct)
        {
            onReceived();
            return Task.CompletedTask;
        }
    }

    private sealed class Silent : IRouteLifecycleListener;
}
