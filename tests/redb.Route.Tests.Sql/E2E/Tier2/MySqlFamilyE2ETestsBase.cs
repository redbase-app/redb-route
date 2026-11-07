using System.Collections;
using System.Data.Common;
using MySqlConnector;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Tier2;

/// <summary>
/// MySQL and MariaDB through MySqlConnector (tier 2, decision №11): the driver's facts for the provider matrix, measured on
/// 2026-09-15, and the connector with the default <c>placeholderStyle=At</c>. Tier 2 runs only with the temporary containers;
/// other runs exclude it with <c>Category!=SqlE2ETier2</c>.
/// </summary>
public abstract class MySqlFamilyE2ETestsBase(MySqlFamilyE2EProvider provider, ITestOutputHelper output) : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    protected MySqlFamilyE2EProvider Provider { get; } = provider;

    protected ITestOutputHelper Output { get; } = output;

    protected SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(Provider);

    public async Task DisposeAsync()
    {
        if (_db is not null)
            await _db.DisposeAsync();
    }

    // ── Driver facts (characterization) ─────────────────────────────

    [Fact]
    public async Task Facts_DbBatch_Savepoints()
    {
        await using var connection = await Db.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        Output.WriteLine($"[fact] {Provider.Name} {connection.ServerVersion}: CanCreateBatch={connection.CanCreateBatch}, " +
                         $"SupportsSavepoints={transaction.SupportsSavepoints}");
        connection.CanCreateBatch.Should().Be(Provider.CanCreateBatch);
        transaction.SupportsSavepoints.Should().Be(Provider.ReportsSavepointSupport);
    }

    [Fact]
    public async Task Facts_DbBatch_DuplicateKey_FailedCommandNotNamed()
    {
        Exception? thrown;
        int position;
        await using (var connection = await Db.OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        await using (var batch = connection.CreateBatch())
        {
            batch.Transaction = transaction;
            foreach (var (id, val) in new[] { (1, "a"), (1, "dup"), (3, "c") })
            {
                var command = batch.CreateBatchCommand();
                command.CommandText = Provider.InsertLiteral(Db.Table, id, val);
                batch.BatchCommands.Add(command);
            }

            thrown = await Outcome.Of(() => batch.ExecuteNonQueryAsync());
            position = (thrown as DbException)?.BatchCommand is { } failed ? batch.BatchCommands.IndexOf(failed) : -1;
            Output.WriteLine($"[fact] {Provider.Name}: DbBatch duplicate key -> {Outcome.Describe(thrown)}, BatchCommand index={position}");
            await transaction.RollbackAsync();
        }

        thrown.Should().BeOfType<MySqlException>(Outcome.Describe(thrown)).Which.Number.Should().Be(1062, "ER_DUP_ENTRY");
        position.Should().Be(-1, "MySqlConnector 2.6 leaves DbException.BatchCommand empty");
    }

    [Fact]
    public async Task Facts_DuplicateKeyInTransaction_RollsBackTheStatementOnly()
    {
        Exception? duplicate;
        await using (var connection = await Db.OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, Provider.InsertLiteral(Db.Table, 1, "a"));
            duplicate = await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, transaction, Provider.InsertLiteral(Db.Table, 1, "dup")));
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, Provider.InsertLiteral(Db.Table, 3, "c"));
            await transaction.CommitAsync();
        }

        duplicate.Should().BeOfType<MySqlException>(Outcome.Describe(duplicate)).Which.Number.Should().Be(1062);
        (await Db.ReadIdsAsync()).Should().Equal(Provider.LoopContinueDuplicateKeyRows, "the failed statement is undone, the transaction goes on");
    }

    [Fact]
    public async Task Facts_ConversionErrorInTransaction_RollsBackTheStatementOnly()
    {
        Exception? conversion;
        await using (var connection = await Db.OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, Provider.InsertLiteral(Db.Table, 1, "a"));
            conversion = await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, transaction, Provider.TransactionKillingLiteral(Db.Table)));
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, Provider.InsertLiteral(Db.Table, 3, "c"));
            await transaction.CommitAsync();
        }

        conversion.Should().BeOfType<MySqlException>(Outcome.Describe(conversion)).Which.Number.Should().Be(1366, "ER_TRUNCATED_WRONG_VALUE_FOR_FIELD");
        (await Db.ReadIdsAsync()).Should().Equal(Provider.LoopContinueTransactionKillingRows);
    }

    [Fact]
    public async Task Facts_SavepointRecoversAfterDuplicateKey()
    {
        Exception? save, rollback;
        await using (var connection = await Db.OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, Provider.InsertLiteral(Db.Table, 1, "a"));
            save = await Outcome.Of(() => transaction.SaveAsync("redb_batch_item"));
            await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, transaction, Provider.InsertLiteral(Db.Table, 1, "dup")));
            rollback = await Outcome.Of(() => transaction.RollbackAsync("redb_batch_item"));
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, Provider.InsertLiteral(Db.Table, 3, "c"));
            await transaction.CommitAsync();
        }

        save.Should().BeNull(Outcome.Describe(save));
        rollback.Should().BeNull("rolling back to the savepoint after a duplicate key works: " + Outcome.Describe(rollback));
        (await Db.ReadIdsAsync()).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Facts_UserVariable_IsTakenForAParameterUnlessAllowUserVariables()
    {
        var refused = await Outcome.Of(() => AddUserVariableAsync(Provider.ConnectionString));
        var allowed = await Outcome.Of(() => AddUserVariableAsync(Provider.ConnectionString + ";AllowUserVariables=True"));

        Output.WriteLine($"[fact] {Provider.Name}: '@u' without AllowUserVariables -> {Outcome.Describe(refused)}");
        refused.Should().NotBeNull("MySqlConnector reads every @name of a command with parameters as a parameter");
        refused!.Message.Should().Contain("Allow User Variables");
        allowed.Should().BeNull(Outcome.Describe(allowed));
    }

    private static async Task AddUserVariableAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SET @u = 5; SELECT @x + @u";
        command.Parameters.AddWithValue("@x", 1);
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(6);
    }

    // ── Connector with placeholderStyle=At ──────────────────────────

    [Fact]
    public async Task Connector_Break_DuplicateKey_NoRows_FailedChunkReported()
    {
        var (_, thrown) = await SqlBatchRun.RunAsync(Db, Provider.InsertSql(Db.Table), Provider.DuplicateKeyItems(),
            breakOnError: true, batchSize: 10);

        thrown.Should().BeOfType<MySqlException>("the provider's exception reaches the route as is: " + Outcome.Describe(thrown));
        thrown!.Data[SqlHeaders.BatchFailedIndex].Should().Be(0, "the driver does not name the failed command: the chunk's first item");
        thrown.Data["redbSql.batchFailedChunk"].Should().Be("0..2", "the failed item is somewhere in this chunk");
        (await Db.ReadIdsAsync()).Should().BeEmpty("a batch that breaks on error rolls everything back");
    }

    [Fact]
    public async Task Connector_Continue_DuplicateKey_OthersCommitted()
    {
        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Provider.InsertSql(Db.Table), Provider.DuplicateKeyItems(),
            breakOnError: false, batchSize: 10);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Headers[SqlHeaders.BatchStrategy].Should().Be("Savepoints");
        SqlBatchRun.BatchErrorIndexes(exchange).Should().Equal(1);
        (await Db.ReadIdsAsync()).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Connector_Continue_ConversionError_OthersCommitted()
    {
        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Provider.TransactionKillingBatchSql(Db.Table), Provider.TransactionKillingItems(),
            breakOnError: false, batchSize: 10);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        SqlBatchRun.BatchErrorIndexes(exchange).Should().Equal(1);
        (await Db.ReadIdsAsync()).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Connector_Break_Batch_ChunksThroughDbBatch()
    {
        var items = Enumerable.Range(1, 5).Select(i => new Dictionary<string, object?> { ["id"] = i, ["val"] = "v" + i }).ToList();

        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Provider.InsertSql(Db.Table), items, breakOnError: null, batchSize: 2);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Headers[SqlHeaders.BatchStrategy].Should().Be("DbBatch");
        (await Db.ReadIdsAsync()).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task Connector_RepeatedPlaceholder_BoundByName()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, Db.CreateConnectionFactory(),
            "SELECT :#x * 100 + :#y * 10 + :#x", new() { ["outputType"] = "Scalar" });
        var exchange = new Exchange(new Message());
        exchange.In.Headers["x"] = 1;
        exchange.In.Headers["y"] = 2;

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Convert.ToInt32(exchange.In.Body).Should().Be(121);
    }

    [Fact]
    public async Task Connector_LiteralsWithAtAndPlaceholderText_PersistedAsWritten()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, Db.CreateConnectionFactory(),
            $"INSERT INTO {Db.Table} (id, val) VALUES (:#id, 'user@example.com :#x')", new() { ["outputType"] = "None" });
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
    public async Task Connector_BackslashEscapes_QuoteEscapedWithBackslash_PersistedAndPlaceholderBound()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, Db.CreateConnectionFactory(),
            $"INSERT INTO {Db.Table} (val, id) VALUES ('it\\'s :#x', :#id)", new() { ["outputType"] = "None", ["backslashEscapes"] = "true" });
        var exchange = new Exchange(new Message());
        exchange.In.Headers["id"] = 7;
        exchange.In.Headers["x"] = "must not be bound";

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeNull("MySQL escapes a quote with a backslash by default: " + Outcome.Describe(thrown));
        var rows = await Db.QueryAsync($"SELECT id, val FROM {Db.Table}");
        rows.Should().ContainSingle();
        Convert.ToInt32(rows[0][0]).Should().Be(7);
        rows[0][1].Should().Be("it's :#x", "text inside the literal is left as written");
    }

    [Fact]
    public async Task Connector_Poll_OnSuccess_MarksEveryRow()
    {
        for (var id = 1; id <= 3; id++)
            await Db.ExecuteAsync(Provider.InsertLiteral(Db.Table, id, "new"));
        await using var context = new RouteContext();
        var parameters = new Dictionary<string, string>
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

    /// <summary>Ids in the <c>redbSql.generatedKeys</c> header: the first column of every collected row.</summary>
    protected static List<int> KeyIds(Exchange exchange)
    {
        exchange.In.Headers.Should().ContainKey(SqlHeaders.GeneratedKeys, "RETURNING rows are collected with outputType=SelectList");
        return exchange.In.Headers[SqlHeaders.GeneratedKeys].Should().BeAssignableTo<IEnumerable>().Subject
            .Cast<IDictionary<string, object?>>()
            .Select(row => Convert.ToInt32(row.Values.First()))
            .ToList();
    }
}
