using System.Collections.Concurrent;
using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;
using Xunit;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// The transaction must decide commit vs. rollback from the exchange's terminal state, not only from
/// whether the inner processor threw: an <c>OnException</c> without <c>Handled(true)</c> leaves the
/// exception on the exchange and returns normally, and that is still a failure.
/// </summary>
public class TransactedProcessorTerminalStateTests
{
    private sealed class TrackingAction : ITransactedAction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }
        public Task Commit(CancellationToken ct = default) { Committed = true; return Task.CompletedTask; }
        public Task Rollback(CancellationToken ct = default) { RolledBack = true; return Task.CompletedTask; }
    }

    /// <summary>A transport step that defers <paramref name="action"/> inside the block, then the rest of the route.</summary>
    private static IProcessor Sending(ITransactedAction action, IProcessor rest) => new DelegateProcessor(async (ex, ct) =>
    {
        TransactedActions.Register(ex, "send-1", action, "test-transport");
        await rest.Process(ex, ct);
    });

    [Fact]
    public async Task UnhandledExceptionLeftOnExchange_RollsBack_DoesNotCommit()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { ci.Arg<IExchange>().Exception = new InvalidOperationException("boom"); return Task.CompletedTask; });
        var action = new TrackingAction();
        var processor = new TransactedProcessor(Sending(action, inner), TransactionPolicy.Default);
        var exchange = new Exchange(new Message { Body = "x" });

        await processor.Process(exchange);

        action.RolledBack.Should().BeTrue("an unhandled failure on the exchange is a failed transaction");
        action.Committed.Should().BeFalse("nothing must be committed on a failed exchange");
        exchange.Exception.Should().NotBeNull("the failure stays on the exchange for the consumer to settle");
    }

    [Fact]
    public async Task HandledExceptionOnExchange_Commits()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ex = ci.Arg<IExchange>();
                ex.Exception = new InvalidOperationException("handled");
                ex.ExceptionHandled = true;
                return Task.CompletedTask;
            });
        var action = new TrackingAction();
        var processor = new TransactedProcessor(Sending(action, inner), TransactionPolicy.Default);
        var exchange = new Exchange(new Message { Body = "x" });

        await processor.Process(exchange);

        action.Committed.Should().BeTrue("a handled exception is a completed exchange");
        action.RolledBack.Should().BeFalse();
    }
}
