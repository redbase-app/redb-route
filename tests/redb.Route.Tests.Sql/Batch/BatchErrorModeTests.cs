using System.Collections;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// Error modes of the batch, decided 2026-09-14 with Apache Camel as the reference: breaking on the first error is the
/// default and rethrows the provider's own exception; continuing past an error is only done under a savepoint per item,
/// refused before any write where savepoints cannot work, and stopped the moment the transaction is found dead.
/// </summary>
/// <remarks>
/// Headers and exception data are addressed by their literal names: the constants arrive with the behaviour (wave 17.1),
/// and these tests are written against the code before it.
/// </remarks>
public sealed class BatchErrorModeTests
{
    private const string Sql = "INSERT INTO t (id) VALUES (:#id)";

    [Fact]
    public async Task Batch_DefaultMode_BreaksOnFirstError()
    {
        var connection = new FakeBatchConnection { OnExecute = FailAt(1) };
        await using var context = new RouteContext();
        var producer = CreateProducer(context, connection, breakOnError: null);
        var exchange = new Exchange(new Message(Items(3)));

        var act = () => producer.Process(exchange, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>("without an explicit mode a failing item fails the whole batch");
        connection.Executions.Should().Be(2, "nothing runs after the failing item");
        connection.Transaction!.RolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task BreakOnError_ThrowsProviderExceptionUnwrapped()
    {
        var connection = new FakeBatchConnection { OnExecute = FailAt(1) };
        await using var context = new RouteContext();
        var producer = CreateProducer(context, connection, breakOnError: true);
        var exchange = new Exchange(new Message(Items(3)));

        // Outcome.Of, not ThrowAsync<T>: FluentAssertions looks inside AggregateException, which would hide the wrapping.
        var thrown = await Outcome.Of(() => producer.Process(exchange, CancellationToken.None));

        thrown.Should().BeOfType<FakeDbException>(
            "the provider's exception reaches the route as is, so OnException<DbException> matches it");
        thrown!.Data["redbSql.batchFailedIndex"].Should().Be(1);
    }

    [Fact]
    public async Task BreakOnError_ReportsTheStrategyInTheHeaderToo()
    {
        var connection = new FakeBatchConnection { OnExecute = FailAt(1) };
        await using var context = new RouteContext();
        var producer = CreateProducer(context, connection, breakOnError: true);
        var exchange = new Exchange(new Message(Items(3)));

        var thrown = await Outcome.Of(() => producer.Process(exchange, CancellationToken.None));

        thrown.Should().NotBeNull();
        exchange.In.Headers["redbSql.batchStrategy"].Should().Be("Commands",
            "an OnException handler reads the failed index by the strategy: a DbBatch index may be the start of a chunk");
    }

    [Fact]
    public async Task BreakOnError_RetriedOnTheSameExchange_ClearsTheFailureHeaders()
    {
        await using var context = new RouteContext();
        var exchange = new Exchange(new Message(Items(3)));
        var failing = CreateProducer(context, new FakeBatchConnection { OnExecute = FailAt(1) }, breakOnError: true);
        (await Outcome.Of(() => failing.Process(exchange, CancellationToken.None))).Should().NotBeNull();
        exchange.In.Headers.Should().ContainKey("redbSql.batchFailedIndex");

        // The route's redelivery runs the same exchange again, this time without the error.
        var succeeding = CreateProducer(context, new FakeBatchConnection(), breakOnError: true);
        var retry = await Outcome.Of(() => succeeding.Process(exchange, CancellationToken.None));

        retry.Should().BeNull(Outcome.Describe(retry));
        exchange.In.Headers.Should().NotContainKey("redbSql.batchFailedIndex",
            "the failure of the first attempt is not a result of the attempt that succeeded");
        exchange.In.Headers["redbSql.batchStrategy"].Should().Be("Commands");
    }

    [Fact]
    public async Task BatchContinue_CleanRunAfterErrors_ClearsTheErrorHeaders()
    {
        await using var context = new RouteContext();
        var exchange = new Exchange(new Message(Items(3)));
        var first = CreateProducer(context, new FakeBatchConnection { Savepoints = SavepointBehavior.Works, OnExecute = FailAt(1) }, breakOnError: false);
        (await Outcome.Of(() => first.Process(exchange, CancellationToken.None))).Should().BeNull();
        exchange.In.Headers.Should().ContainKey("redbSql.batchErrors").And.ContainKey("redbSql.error");

        var second = CreateProducer(context, new FakeBatchConnection { Savepoints = SavepointBehavior.Works }, breakOnError: false);
        var clean = await Outcome.Of(() => second.Process(exchange, CancellationToken.None));

        clean.Should().BeNull(Outcome.Describe(clean));
        exchange.In.Headers.Should().NotContainKey("redbSql.batchErrors", "no item of this run failed");
        exchange.In.Headers.Should().NotContainKey("redbSql.error", "the errors of an earlier step do not describe this one");
    }

    [Fact]
    public async Task BatchContinue_SavepointsUnavailable_FailsBeforeFirstWrite()
    {
        var connection = new FakeBatchConnection { Savepoints = SavepointBehavior.Unsupported, OnExecute = FailAt(1) };
        await using var context = new RouteContext();
        var producer = CreateProducer(context, connection, breakOnError: false);
        var exchange = new Exchange(new Message(Items(3)));

        var act = () => producer.Process(exchange, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("continuing past an error cannot be honest without savepoints");
        connection.Executions.Should().Be(0, "the refusal comes before anything is written");
    }

    [Fact]
    public async Task BatchContinue_StatementLevelError_ContinuesAndReportsItemErrors()
    {
        var connection = new FakeBatchConnection { Savepoints = SavepointBehavior.Works, OnExecute = FailAt(1) };
        await using var context = new RouteContext();
        var producer = CreateProducer(context, connection, breakOnError: false);
        var exchange = new Exchange(new Message(Items(3)));

        await producer.Process(exchange, CancellationToken.None);

        connection.Executions.Should().Be(3);
        connection.Transaction!.SavepointLog.Should().Contain(entry => entry.StartsWith("rollback:"),
            "the failed item is undone to its savepoint, the others stay");
        connection.Transaction.Committed.Should().BeTrue();
        exchange.In.Headers.Should().ContainKey("redbSql.batchErrors");
        var errors = ((IEnumerable)exchange.In.Headers["redbSql.batchErrors"]!).Cast<object>().ToList();
        errors.Should().ContainSingle();
        errors[0].GetType().GetProperty("Index")!.GetValue(errors[0]).Should().Be(1);
    }

    [Fact]
    public async Task BatchContinue_TransactionDeadAfterItemError_NoFurtherCommands()
    {
        var connection = new FakeBatchConnection { Savepoints = SavepointBehavior.RollbackToSavepointFails, OnExecute = FailAt(1) };
        await using var context = new RouteContext();
        var producer = CreateProducer(context, connection, breakOnError: false);
        var exchange = new Exchange(new Message(Items(3)));

        var thrown = await Outcome.Of(() => producer.Process(exchange, CancellationToken.None));

        thrown.Should().BeOfType<FakeDbException>("the item's own error ends a batch whose transaction is gone");
        connection.Executions.Should().Be(2,
            "after a failed rollback to the savepoint no statement may be sent — SQL Server would run it in autocommit");
    }

    private static Func<int, CancellationToken, int> FailAt(int failingIndex) => (index, _) =>
        index == failingIndex ? throw new FakeDbException($"fake: item {index} violates a constraint") : 1;

    private static IProducer CreateProducer(RouteContext context, FakeBatchConnection connection, bool? breakOnError)
    {
        var parameters = new Dictionary<string, string>
        {
            ["outputType"] = "None",
            ["batchSize"] = "10",
        };
        if (breakOnError is { } mode)
            parameters["breakBatchOnError"] = mode ? "true" : "false";

        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(connection), Sql, parameters);
        return endpoint.CreateProducer();
    }

    private static List<Dictionary<string, object?>> Items(int count) =>
        Enumerable.Range(1, count).Select(i => new Dictionary<string, object?> { ["id"] = i }).ToList();
}
