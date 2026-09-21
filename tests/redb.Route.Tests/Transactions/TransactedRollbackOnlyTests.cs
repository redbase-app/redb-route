using System.Transactions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Transactions;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// <c>.RollbackAll()</c> is Camel's <c>markRollbackOnly()</c>: the route stops, the unit of work is rolled back without an
/// exception (the database and the deferred sends), and the consumer does not acknowledge the message, so the broker
/// delivers it again.
/// </summary>
public class TransactedRollbackOnlyTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private sealed class LoggedSend(string name, List<string> log) : ITransactedAction
    {
        public Task Commit(CancellationToken ct = default) { lock (log) log.Add($"commit:{name}"); return Task.CompletedTask; }
        public Task Rollback(CancellationToken ct = default) { lock (log) log.Add($"rollback:{name}"); return Task.CompletedTask; }
    }

    /// <summary>A database stand-in: a resource enlisted in the ambient transaction that logs how it ends.</summary>
    private sealed class LoggedDatabase(List<string> log) : IEnlistmentNotification
    {
        public void Prepare(PreparingEnlistment enlistment) => enlistment.Prepared();
        public void Commit(Enlistment enlistment) { lock (log) log.Add("db:commit"); enlistment.Done(); }
        public void Rollback(Enlistment enlistment) { lock (log) log.Add("db:rollback"); enlistment.Done(); }
        public void InDoubt(Enlistment enlistment) => enlistment.Done();
    }

    private static void Work(IExchange exchange, List<string> log)
    {
        Transaction.Current!.EnlistVolatile(new LoggedDatabase(log), EnlistmentOptions.None);
        TransactedActions.Register(exchange, $"send-{Guid.NewGuid():N}", new LoggedSend("send", log), "broker:send");
    }

    private async Task Run(string uri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("m")));   // no exception: a rollback-only exchange is not an error
    }

    [Fact]
    public async Task RollbackAll_inside_a_block_rolls_back_the_database_and_the_sends_and_stops_the_route()
    {
        var log = new List<string>();
        _context.AddRoutes(r => r.From("direct://rb-block")
            .Transacted()
                .Process(ex => Work(ex, log))
                .RollbackAll()
                .Process(_ => log.Add("step after rollbackAll"))
            .End()
            .Process(_ => log.Add("step after the block")));

        await Run("direct://rb-block");

        log.Should().Contain("db:rollback").And.Contain("rollback:send");
        log.Should().NotContain("db:commit").And.NotContain("commit:send");
        log.Should().NotContain("step after rollbackAll").And.NotContain("step after the block", "the route stops");
    }

    [Fact]
    public async Task RollbackAll_in_a_joined_inner_block_rolls_back_the_whole_unit_of_work()
    {
        var log = new List<string>();
        _context.AddRoutes(r => r.From("direct://rb-joined")
            .Transacted()
                .Process(ex => Work(ex, log))
                .Transacted()
                    .RollbackAll()
                .End()
            .End());

        await Run("direct://rb-joined");

        log.Should().Contain("db:rollback").And.Contain("rollback:send");
        log.Should().NotContain("db:commit").And.NotContain("commit:send");
    }

    [Fact]
    public async Task RollbackAll_after_BeginTransaction_rolls_that_transaction_back()
    {
        var log = new List<string>();
        _context.AddRoutes(r => r.From("direct://rb-imperative")
            .BeginTransaction()
            .Process(ex => Work(ex, log))
            .RollbackAll()
            .CommitTransaction());

        await Run("direct://rb-imperative");

        log.Should().Contain("db:rollback").And.Contain("rollback:send");
        log.Should().NotContain("db:commit").And.NotContain("commit:send");
    }

    [Fact]
    public void Consumer_treats_a_rollback_only_exchange_as_a_failed_unit_of_work()
    {
        var exchange = new Exchange(new Message("m"));
        exchange.Properties["RollbackOnly"] = true;   // what .RollbackAll() leaves on the exchange

        var settle = () => exchange.ThrowIfUnhandledFailure();

        settle.Should().Throw<InvalidOperationException>("the consumer must not acknowledge a rolled-back unit of work")
            .WithMessage("*rollback*");
    }
}
