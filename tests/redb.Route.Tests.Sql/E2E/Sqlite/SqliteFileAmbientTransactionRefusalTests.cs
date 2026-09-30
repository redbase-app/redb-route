using System.Transactions;
using redb.Route.Core;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Sqlite;

/// <summary>
/// Microsoft.Data.Sqlite does not enlist in System.Transactions. A <c>sql:</c> step inside a <c>.Transacted()</c> block
/// used to write in autocommit past the block's transaction: a rolled-back block left its rows behind. The step now
/// refuses before it writes; outside a block it works as always.
/// </summary>
public sealed class SqliteFileAmbientTransactionRefusalTests : IAsyncLifetime
{
    private readonly SqliteFileE2EProvider _provider = new();
    private SqlE2EDatabase _db = null!;

    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(_provider);

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _provider.Dispose();
    }

    private async Task<Exception?> RunStatement()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateConnectionFactory(),
            $"INSERT INTO {_db.Table}(id, val) VALUES (1, 'a')", new Dictionary<string, string> { ["outputType"] = "None" });
        var producer = endpoint.CreateProducer();
        return await Outcome.Of(() => producer.Process(new Exchange(new Message(null)), CancellationToken.None));
    }

    [Fact]
    public async Task A_statement_inside_a_transaction_is_refused_before_it_writes()
    {
        Exception? thrown;
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            thrown = await RunStatement();
            // Not completed: the block rolls back.
        }

        thrown.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("does not join");
        (await _db.ReadIdsAsync()).Should().BeEmpty("a rolled-back block must not leave the step's row behind");
    }

    [Fact]
    public async Task A_batch_inside_a_transaction_is_refused_before_it_writes()
    {
        var items = new List<Dictionary<string, object?>> { new() { ["id"] = 1, ["val"] = "a" } };
        Exception? thrown;
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            (_, thrown) = await SqlBatchRun.RunAsync(_db, _provider.InsertSql(_db.Table), items, breakOnError: true);
        }

        thrown.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("does not join");
        (await _db.ReadIdsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Outside_a_transaction_the_statement_runs_as_always()
    {
        (await RunStatement()).Should().BeNull();

        (await _db.ReadIdsAsync()).Should().Equal(1);
    }
}
