using System.Collections.Concurrent;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Transactions;
using Xunit;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// End-to-end: a failure that an <c>OnException</c> without <c>Handled(true)</c> leaves on the exchange
/// must still roll the enclosing <c>.Transacted()</c> block back, whichever way it reaches the transaction.
/// </summary>
public class TransactedRouteTerminalStateTests
{
    private sealed class TrackingAction : ITransactedAction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }
        public Task Commit(CancellationToken ct = default) { Committed = true; return Task.CompletedTask; }
        public Task Rollback(CancellationToken ct = default) { RolledBack = true; return Task.CompletedTask; }
    }

    private static void Register(IExchange ex, ITransactedAction action)
    {
        var dict = (ConcurrentDictionary<string, ITransactedAction>)ex.Properties[TransactedProcessor.TransactActionPropertyKey]!;
        dict["send-1"] = action;
    }

    /// <summary>
    /// The in-route OnException is hoisted outside the transaction: the throw reaches TransactedProcessor directly.
    /// </summary>
    [Fact]
    public async Task OnExceptionWithoutHandled_InsideTransacted_RollsBack()
    {
        var context = new RouteContext();
        var action = new TrackingAction();

        context.AddRoutes(r =>
        {
            r.From("direct://tx-oe-in")
                .Transacted()
                    .OnException<InvalidOperationException>()   // no Handled(): failure stays on the exchange
                        .Log("seen")
                    .End()
                    .Process((ex, _) => { Register(ex, action); throw new InvalidOperationException("boom"); })
                .End();
        });

        await AssertRolledBack(context, "direct://tx-oe-in", action);
    }

    /// <summary>
    /// A sub-route called from the transacted body handles the failure (handled:false) and returns normally —
    /// the caller's exchange carries the exception without a throw.
    /// </summary>
    [Fact]
    public async Task SubRouteOnExceptionWithoutHandled_InsideTransacted_RollsBack()
    {
        var context = new RouteContext();
        var action = new TrackingAction();

        context.AddRoutes(r =>
        {
            r.From("direct://tx-sub-in")
                .Transacted()
                    .To("direct://tx-sub-work")
                .End();

            r.From("direct://tx-sub-work")
                .OnException<InvalidOperationException>()   // no Handled(): the sub-route returns normally
                    .Log("seen")
                .End()
                .Process((ex, _) => { Register(ex, action); throw new InvalidOperationException("boom"); });
        });

        await AssertRolledBack(context, "direct://tx-sub-in", action);
    }

    private static async Task AssertRolledBack(RouteContext context, string uri, TrackingAction action)
    {
        await context.Start();
        try
        {
            var producer = context.GetEndpoint(uri).CreateProducer();
            await producer.Start();

            var exchange = new Exchange(new Message { Body = "x" });
            await producer.Process(exchange);   // returns normally: handled:false keeps the failure on the exchange

            exchange.Exception.Should().NotBeNull("handled:false keeps the failure on the exchange");
            exchange.ExceptionHandled.Should().BeFalse();
            action.RolledBack.Should().BeTrue("an unhandled failure must roll the transaction back");
            action.Committed.Should().BeFalse();
        }
        finally
        {
            await context.DisposeAsync();
        }
    }
}
