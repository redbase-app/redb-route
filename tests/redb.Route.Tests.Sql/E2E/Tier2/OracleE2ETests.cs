using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Tier2;

/// <summary>
/// Oracle Free 23 through ODP.NET 23.26 (tier 2, decision №11): the driver's facts for the provider matrix, measured on
/// 2026-09-15, and the connector with <c>placeholderStyle=Colon</c> — a batch that breaks on a duplicate key leaves nothing, a
/// batch that goes on keeps the other rows, a repeated <c>:#name</c> binds by position, a poll marks its rows. Tier 2 runs
/// only with the temporary <c>route-oracle</c> container; other runs exclude it with <c>Category!=SqlE2ETier2</c>.
/// </summary>
[Trait("Category", "SqlE2ETier2")]
[Trait("SqlProvider", "oracle")]
public sealed class OracleE2ETests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly Dictionary<string, string> Colon = new() { ["placeholderStyle"] = "Colon" };
    private readonly OracleE2EProvider _provider = new();
    private SqlE2EDatabase? _db;

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(_provider);

    public async Task DisposeAsync()
    {
        if (_db is not null)
            await _db.DisposeAsync();
    }

    // ── Driver facts (characterization) ─────────────────────────────

    [Fact]
    public async Task Facts_NoDbBatch_PositionalBinding_Savepoints()
    {
        await using var connection = await Db.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = (OracleCommand)connection.CreateCommand();
        command.Transaction = (OracleTransaction)transaction;
        command.CommandText = "SELECT :a * 100 + :b FROM DUAL";
        command.Parameters.Add(new OracleParameter("b", 2));
        command.Parameters.Add(new OracleParameter("a", 1));
        var result = Convert.ToInt32(await command.ExecuteScalarAsync());

        output.WriteLine($"[fact] oracle: CanCreateBatch={connection.CanCreateBatch}, BindByName={command.BindByName}, " +
                         $"result={result}, SupportsSavepoints={transaction.SupportsSavepoints}");
        connection.CanCreateBatch.Should().Be(_provider.CanCreateBatch, "ODP.NET 23.26 has no DbBatch: a batch runs as commands");
        command.BindByName.Should().BeFalse("ODP.NET binds by position unless told otherwise");
        result.Should().Be(201, "parameters added b then a bind to :a and :b in the order they were added");
        transaction.SupportsSavepoints.Should().Be(_provider.ReportsSavepointSupport);
    }

    [Fact]
    public async Task Facts_DuplicateKeyInTransaction_RollsBackTheStatementOnly()
    {
        Exception? duplicate;
        await using (var connection = await Db.OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, _provider.InsertLiteral(Db.Table, 1, "a"));
            duplicate = await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, transaction, _provider.InsertLiteral(Db.Table, 1, "dup")));
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, _provider.InsertLiteral(Db.Table, 3, "c"));
            await transaction.CommitAsync();
        }

        duplicate.Should().BeOfType<OracleException>(Outcome.Describe(duplicate)).Which.Number.Should().Be(1, "ORA-00001");
        (await Db.ReadIdsAsync()).Should().Equal(_provider.LoopContinueDuplicateKeyRows, "the failed statement is undone, the transaction goes on");
    }

    [Fact]
    public async Task Facts_SavepointRecoversAfterDuplicateKey()
    {
        Exception? save, rollback;
        await using (var connection = await Db.OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, _provider.InsertLiteral(Db.Table, 1, "a"));
            save = await Outcome.Of(() => transaction.SaveAsync("redb_batch_item"));
            await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, transaction, _provider.InsertLiteral(Db.Table, 1, "dup")));
            rollback = await Outcome.Of(() => transaction.RollbackAsync("redb_batch_item"));
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, _provider.InsertLiteral(Db.Table, 3, "c"));
            await transaction.CommitAsync();
        }

        save.Should().BeNull(Outcome.Describe(save));
        rollback.Should().BeNull("rolling back to the savepoint after a duplicate key works: " + Outcome.Describe(rollback));
        (await Db.ReadIdsAsync()).Should().Equal(1, 3);
    }

    // ── Connector with placeholderStyle=Colon ───────────────────────

    [Fact]
    public async Task Connector_Break_DuplicateKey_NoRows()
    {
        var (_, thrown) = await SqlBatchRun.RunAsync(Db, _provider.InsertSql(Db.Table), _provider.DuplicateKeyItems(),
            breakOnError: true, batchSize: 10, Colon);

        thrown.Should().BeOfType<OracleException>("the provider's exception reaches the route as is: " + Outcome.Describe(thrown));
        thrown!.Data[SqlHeaders.BatchFailedIndex].Should().Be(1);
        (await Db.ReadIdsAsync()).Should().BeEmpty("a batch that breaks on error rolls everything back");
    }

    [Fact]
    public async Task Connector_Continue_DuplicateKey_OthersCommitted()
    {
        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, _provider.InsertSql(Db.Table), _provider.DuplicateKeyItems(),
            breakOnError: false, batchSize: 10, Colon);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Headers[SqlHeaders.BatchStrategy].Should().Be("Savepoints");
        SqlBatchRun.BatchErrorIndexes(exchange).Should().Equal(1);
        (await Db.ReadIdsAsync()).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Connector_Break_Batch_RunsAsCommands()
    {
        var items = Enumerable.Range(1, 5).Select(i => new Dictionary<string, object?> { ["id"] = i, ["val"] = "v" + i }).ToList();

        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, _provider.InsertSql(Db.Table), items, breakOnError: null, batchSize: 2, Colon);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Headers[SqlHeaders.BatchStrategy].Should().Be("Commands", "ODP.NET has no DbBatch");
        (await Db.ReadIdsAsync()).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task Connector_ColonStyle_RepeatedPlaceholder_BoundInOrder()
    {
        await using var context = new RouteContext();
        var parameters = new Dictionary<string, string>(Colon) { ["outputType"] = "Scalar" };
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, Db.CreateConnectionFactory(),
            "SELECT :#x * 100 + :#y * 10 + :#x FROM DUAL", parameters);
        var exchange = new Exchange(new Message());
        exchange.In.Headers["x"] = 1;
        exchange.In.Headers["y"] = 2;

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Convert.ToInt32(exchange.In.Body).Should().Be(121, "every occurrence gets its parameter, in order");
    }

    [Fact]
    public async Task Connector_LiteralsWithAtAndPlaceholderText_PersistedAsWritten()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, Db.CreateConnectionFactory(),
            $"INSERT INTO {Db.Table} (id, val) VALUES (:#id, 'user@example.com :#x')", new(Colon) { ["outputType"] = "None" });
        var exchange = new Exchange(new Message());
        exchange.In.Headers["id"] = 7;

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        var rows = await Db.QueryAsync($"SELECT id, val FROM {Db.Table}");
        rows.Should().ContainSingle();
        Convert.ToInt32(rows[0][0]).Should().Be(7);
        rows[0][1].Should().Be("user@example.com :#x");
    }

    [Fact]
    public async Task Connector_Poll_OnSuccess_MarksEveryRow()
    {
        for (var id = 1; id <= 3; id++)
            await Db.ExecuteAsync(_provider.InsertLiteral(Db.Table, id, "new"));
        await using var context = new RouteContext();
        var parameters = new Dictionary<string, string>(Colon)
        {
            ["mode"] = "Poll",
            ["transacted"] = "true",
            ["onSuccess"] = $"UPDATE {Db.Table} SET val = 'done' WHERE id = :#id",
            ["repeatCount"] = "1",
        };
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, Db.CreateConnectionFactory(), $"SELECT id, val FROM {Db.Table} ORDER BY id", parameters);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var thrown = await Outcome.Of(() => ((SqlConsumer)endpoint.CreateConsumer(processor)).Poll(CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        var values = (await Db.QueryAsync($"SELECT val FROM {Db.Table} ORDER BY id")).Select(r => r[0]);
        values.Should().Equal("done", "done", "done");
    }
}
