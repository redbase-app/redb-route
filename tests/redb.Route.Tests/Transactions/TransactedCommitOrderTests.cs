using System.Collections.Concurrent;
using System.Transactions;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;
using Xunit;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// The order a unit of work commits in: the database first, the brokers after. The next service is told about
/// the work through a message and then reads the work by its id, so the failure window must be "written but not
/// announced" (a redelivery the idempotent consumer absorbs), never "announced but not written" (a message
/// pointing at rows nobody can see).
/// </summary>
public class TransactedCommitOrderTests
{
    private sealed class RecordingAction : ITransactedAction
    {
        public bool? AmbientTransactionAtCommit;
        public int Commits;
        public int Rollbacks;
        public Exception? FailWith;

        public Task Commit(CancellationToken ct = default)
        {
            AmbientTransactionAtCommit = Transaction.Current is not null;
            Commits++;
            return FailWith is null ? Task.CompletedTask : Task.FromException(FailWith);
        }

        public Task Rollback(CancellationToken ct = default)
        {
            Rollbacks++;
            return Task.CompletedTask;
        }
    }

    private static ConcurrentDictionary<string, ITransactedAction> Register(IExchange exchange, params (string Key, ITransactedAction Action)[] actions)
    {
        var bag = new ConcurrentDictionary<string, ITransactedAction>();
        foreach (var (key, action) in actions)
            bag[key] = action;
        exchange.Properties[TransactedProcessor.TransactActionPropertyKey] = bag;
        return bag;
    }

    [Fact]
    public async Task Broker_actions_commit_after_the_database_transaction_is_closed()
    {
        var send = new RecordingAction();
        ConcurrentDictionary<string, ITransactedAction>? bag = null;
        var processor = new TransactedProcessor(
            new DelegateProcessor(ex => bag = Register(ex, ("send-1", send))),
            new TransactionPolicy());

        await processor.Process(new Exchange(new Message("m")));

        send.Commits.Should().Be(1);
        send.AmbientTransactionAtCommit.Should().BeFalse(
            "the database transaction must be complete and closed before a message goes out");
        bag.Should().NotBeNull();
        bag!.Should().BeEmpty("a committed action is done and must not be committed again by a later attempt");
    }

    [Fact]
    public async Task A_rolled_back_attempt_leaves_no_actions_behind()
    {
        var send = new RecordingAction();
        ConcurrentDictionary<string, ITransactedAction>? bag = null;
        var processor = new TransactedProcessor(
            new DelegateProcessor(ex =>
            {
                bag = Register(ex, ("send-1", send));
                throw new InvalidOperationException("boom");
            }),
            new TransactionPolicy());

        var act = () => processor.Process(new Exchange(new Message("m")));

        await act.Should().ThrowAsync<InvalidOperationException>();
        send.Rollbacks.Should().Be(1);
        send.Commits.Should().Be(0);
        bag!.Should().BeEmpty("the next attempt starts from an empty set, not from the failed one");
    }

    [Fact]
    public async Task A_broker_that_fails_after_the_database_committed_does_not_swallow_the_failure()
    {
        var send = new RecordingAction { FailWith = new InvalidOperationException("broker down") };
        var processor = new TransactedProcessor(
            new DelegateProcessor(ex => Register(ex, ("send-1", send))),
            new TransactionPolicy());

        var act = () => processor.Process(new Exchange(new Message("m")));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*broker down*");
        send.AmbientTransactionAtCommit.Should().BeFalse("the database was already committed and cannot be undone");
    }

    [Fact]
    public async Task An_unhandled_failure_left_on_the_exchange_still_rolls_everything_back()
    {
        var send = new RecordingAction();
        var processor = new TransactedProcessor(
            new DelegateProcessor(ex =>
            {
                Register(ex, ("send-1", send));
                ex.Exception = new InvalidOperationException("handled:false");
            }),
            new TransactionPolicy());

        await processor.Process(new Exchange(new Message("m")));

        send.Rollbacks.Should().Be(1);
        send.Commits.Should().Be(0);
    }
}
