using System.Transactions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// Blocks inside blocks, as in Camel: a <c>Required</c> block inside a running transaction is part of that unit of work,
/// so its deferred sends leave with the enclosing commit; a <c>RequiresNew</c> or <c>Suppress</c> block is a unit of work
/// of its own and settles only its own sends. The imperative <c>BeginTransaction</c> ... <c>CommitTransaction</c> follows
/// the same rules and commits in the same order: the database first, the sends after it.
/// </summary>
public class TransactedPropagationTests
{
    private sealed class LoggedSend(string name, List<string> log) : ITransactedAction
    {
        public Task Commit(CancellationToken ct = default)
        {
            lock (log) log.Add($"commit:{name}");
            return Task.CompletedTask;
        }

        public Task Rollback(CancellationToken ct = default)
        {
            lock (log) log.Add($"rollback:{name}");
            return Task.CompletedTask;
        }
    }

    /// <summary>A database stand-in: a resource enlisted in the ambient transaction that logs its commit.</summary>
    private sealed class LoggedDatabase(List<string> log) : IEnlistmentNotification
    {
        public void Prepare(PreparingEnlistment enlistment) => enlistment.Prepared();

        public void Commit(Enlistment enlistment)
        {
            lock (log) log.Add("db:commit");
            enlistment.Done();
        }

        public void Rollback(Enlistment enlistment)
        {
            lock (log) log.Add("db:rollback");
            enlistment.Done();
        }

        public void InDoubt(Enlistment enlistment) => enlistment.Done();
    }

    private static void Send(IExchange exchange, string name, List<string> log) =>
        TransactedActions.Register(exchange, $"{name}-{Guid.NewGuid():N}", new LoggedSend(name, log), $"broker:{name}");

    private static void WriteDatabase(List<string> log) =>
        Transaction.Current!.EnlistVolatile(new LoggedDatabase(log), EnlistmentOptions.None);

    private static TransactedProcessor Block(TransactionPolicy policy, Func<IExchange, CancellationToken, Task> body) =>
        new(new DelegateProcessor(body), policy);

    private static TransactedProcessor Block(TransactionPolicy policy, Action<IExchange> body) =>
        new(new DelegateProcessor(body), policy);

    public static TheoryData<string> OwnUnitsOfWork => new() { "RequiresNew", "Suppress" };

    // ── .Transacted() inside .Transacted() ──

    [Fact]
    public async Task Required_inner_block_leaves_its_sends_to_the_enclosing_block()
    {
        var log = new List<string>();

        await Block(TransactionPolicy.Default, async (ex, ct) =>
        {
            Send(ex, "outer", log);
            await Block(TransactionPolicy.Default, inner => Send(inner, "inner", log)).Process(ex, ct);
            log.Add("inner block ended");
        }).Process(new Exchange(new Message("m")));

        log[0].Should().Be("inner block ended", "a joined block commits nothing of its own");
        log.Skip(1).Should().BeEquivalentTo("commit:outer", "commit:inner");
    }

    [Fact]
    public async Task Required_inner_block_commits_its_sends_after_the_enclosing_database_commit()
    {
        var log = new List<string>();

        await Block(TransactionPolicy.Default, async (ex, ct) =>
        {
            WriteDatabase(log);
            await Block(TransactionPolicy.Default, inner => Send(inner, "inner", log)).Process(ex, ct);
        }).Process(new Exchange(new Message("m")));

        log.Should().Equal("db:commit", "commit:inner");
    }

    [Theory]
    [MemberData(nameof(OwnUnitsOfWork))]
    public async Task Inner_block_of_its_own_settles_only_its_own_sends(string policy)
    {
        var log = new List<string>();

        await Block(TransactionPolicy.Default, async (ex, ct) =>
        {
            Send(ex, "outer", log);
            await Block(TransactionPolicy.FromName(policy), inner => Send(inner, "inner", log)).Process(ex, ct);
            log.Add("inner block ended");
        }).Process(new Exchange(new Message("m")));

        log.Should().Equal("commit:inner", "inner block ended", "commit:outer");
    }

    [Theory]
    [MemberData(nameof(OwnUnitsOfWork))]
    public async Task Failed_inner_block_of_its_own_rolls_back_only_its_own_sends(string policy)
    {
        var log = new List<string>();

        await Block(TransactionPolicy.Default, async (ex, ct) =>
        {
            Send(ex, "outer", log);
            var inner = Block(TransactionPolicy.FromName(policy), (inner, _) =>
            {
                Send(inner, "inner", log);
                throw new InvalidOperationException("inner unit of work failed");
            });
            var failed = () => inner.Process(ex, ct);
            await failed.Should().ThrowAsync<InvalidOperationException>();
        }).Process(new Exchange(new Message("m")));

        log.Should().Equal("rollback:inner", "commit:outer");
    }

    // ── BeginTransaction ... CommitTransaction ──

    [Fact]
    public async Task CommitTransaction_commits_the_database_before_the_sends()
    {
        var log = new List<string>();
        var exchange = new Exchange(new Message("m"));

        await new BeginTransactionProcessor().Process(exchange);
        WriteDatabase(log);
        Send(exchange, "send", log);
        await new CommitTransactionProcessor().Process(exchange);

        log.Should().Equal("db:commit", "commit:send");
    }

    [Fact]
    public async Task CommitTransaction_inside_a_transacted_block_leaves_the_sends_to_the_block()
    {
        var log = new List<string>();

        await Block(TransactionPolicy.Default, async (ex, ct) =>
        {
            Send(ex, "outer", log);
            await new BeginTransactionProcessor().Process(ex, ct);
            Send(ex, "inner", log);
            await new CommitTransactionProcessor().Process(ex, ct);
            log.Add("commitTransaction ran");
        }).Process(new Exchange(new Message("m")));

        log[0].Should().Be("commitTransaction ran", "a joined imperative block commits nothing of its own");
        log.Skip(1).Should().BeEquivalentTo("commit:outer", "commit:inner");
    }

    [Fact]
    public async Task RequiresNew_imperative_block_inside_a_transacted_block_settles_only_its_own_sends()
    {
        var log = new List<string>();

        await Block(TransactionPolicy.Default, async (ex, ct) =>
        {
            Send(ex, "outer", log);
            await new BeginTransactionProcessor(TransactionPolicy.RequiresNew).Process(ex, ct);
            Send(ex, "inner", log);
            await new CommitTransactionProcessor().Process(ex, ct);
            log.Add("commitTransaction ran");
        }).Process(new Exchange(new Message("m")));

        log.Should().Equal("commit:inner", "commitTransaction ran", "commit:outer");
    }

    // ── The order the deferred sends leave in ──

    [Fact]
    public async Task Deferred_sends_commit_in_the_order_the_route_registered_them()
    {
        var log = new List<string>();
        var expected = Enumerable.Range(1, 20).Select(i => $"commit:send-{i:D2}").ToList();

        await Block(TransactionPolicy.Default, ex =>
        {
            for (var i = 1; i <= 20; i++)
                TransactedActions.Register(ex, $"key-{Guid.NewGuid():N}", new LoggedSend($"send-{i:D2}", log), "broker");
        }).Process(new Exchange(new Message("m")));

        log.Should().Equal(expected, "two sends to one queue must not arrive swapped");
    }

    // ── The transacted parameter of a producer ──

    [Fact]
    public async Task Unset_transacted_defers_inside_a_block_and_sends_at_once_outside()
    {
        var exchange = new Exchange(new Message("m"));
        bool? inside = null;

        await Block(TransactionPolicy.Default, ex => inside = TransactedActions.Defers(ex, transacted: null))
            .Process(exchange);

        inside.Should().BeTrue();
        TransactedActions.Defers(exchange, transacted: null).Should().BeFalse();
    }

    [Fact]
    public async Task Transacted_false_sends_at_once_even_inside_a_block()
    {
        bool? inside = null;

        await Block(TransactionPolicy.Default, ex => inside = TransactedActions.Defers(ex, transacted: false))
            .Process(new Exchange(new Message("m")));

        inside.Should().BeFalse();
    }

    [Fact]
    public void Transacted_true_always_defers_so_that_outside_a_block_registration_refuses_it()
    {
        TransactedActions.Defers(new Exchange(new Message("m")), transacted: true).Should().BeTrue();
    }
}
