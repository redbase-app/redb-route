using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// The set of deferred transport actions belongs to the block that opened it, <c>.Transacted()</c> or
/// <c>.BeginTransaction()</c> ... <c>.CommitTransaction()</c>. A transacted send outside a block, or after the
/// block has closed, has nobody to commit it, so it is refused rather than deferred into a set nobody reads.
/// </summary>
public class TransactedActionsOwnershipTests
{
    private sealed class NoopAction : ITransactedAction
    {
        public Task Commit(CancellationToken ct = default) => Task.CompletedTask;
        public Task Rollback(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static Exchange NewExchange() => new(new Message("body"));

    private static Task InTransaction(IExchange exchange, Func<IExchange, CancellationToken, Task> body) =>
        new TransactedProcessor(new DelegateProcessor(body), TransactionPolicy.Default).Process(exchange);

    private static Action RegisterSend(IExchange exchange) =>
        () => TransactedActions.Register(exchange, $"send-{Guid.NewGuid():N}", new NoopAction(), "broker:orders");

    [Fact]
    public void Register_outside_any_block_throws_naming_the_endpoint()
    {
        var exchange = NewExchange();

        RegisterSend(exchange).Should().Throw<InvalidOperationException>()
            .WithMessage("'broker:orders' is transacted*Transacted()*");
    }

    [Fact]
    public async Task Transacted_block_opens_the_set_on_entry_and_removes_it_on_exit()
    {
        var exchange = NewExchange();
        bool? activeInside = null;

        await InTransaction(exchange, (ex, _) =>
        {
            activeInside = TransactedActions.IsActive(ex);
            return Task.CompletedTask;
        });

        activeInside.Should().BeTrue();
        TransactedActions.IsActive(exchange).Should().BeFalse();
        RegisterSend(exchange).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Transacted_block_removes_the_set_when_it_rolls_back()
    {
        var exchange = NewExchange();

        var failed = () => InTransaction(exchange, (_, _) => throw new InvalidOperationException("unit of work failed"));

        await failed.Should().ThrowAsync<InvalidOperationException>().WithMessage("unit of work failed");
        TransactedActions.IsActive(exchange).Should().BeFalse();
    }

    [Fact]
    public async Task Nested_transacted_block_leaves_the_set_to_the_outer_block()
    {
        var exchange = NewExchange();
        bool? activeAfterInner = null;

        await InTransaction(exchange, async (ex, ct) =>
        {
            await InTransaction(ex, (_, _) => Task.CompletedTask);
            activeAfterInner = TransactedActions.IsActive(ex);
        });

        activeAfterInner.Should().BeTrue("the outer block is still running");
        TransactedActions.IsActive(exchange).Should().BeFalse();
    }

    [Fact]
    public async Task Send_after_commitTransaction_is_refused()
    {
        var exchange = NewExchange();

        await new BeginTransactionProcessor().Process(exchange);
        TransactedActions.IsActive(exchange).Should().BeTrue();
        await new CommitTransactionProcessor().Process(exchange);

        TransactedActions.IsActive(exchange).Should().BeFalse();
        RegisterSend(exchange).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Send_after_rollbackTransaction_is_refused()
    {
        var exchange = NewExchange();

        await new BeginTransactionProcessor().Process(exchange);
        await new RollbackTransactionProcessor().Process(exchange);

        TransactedActions.IsActive(exchange).Should().BeFalse();
        RegisterSend(exchange).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Imperative_transaction_inside_transacted_leaves_the_outer_set()
    {
        var exchange = NewExchange();
        bool? activeAfterCommit = null;

        await InTransaction(exchange, async (ex, ct) =>
        {
            await new BeginTransactionProcessor().Process(ex, ct);
            await new CommitTransactionProcessor().Process(ex, ct);
            activeAfterCommit = TransactedActions.IsActive(ex);
        });

        activeAfterCommit.Should().BeTrue("the enclosing .Transacted() opened the set and still owns it");
    }
}
