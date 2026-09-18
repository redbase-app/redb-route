using System.Collections;
using FirebirdSql.Data.FirebirdClient;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Tier2;

/// <summary>
/// Firebird 5 through FirebirdClient 10.3 (tier 2, decision №11): the driver's facts for the provider matrix, measured on
/// 2026-09-15, and the connector with the default <c>placeholderStyle=At</c>. Tier 2 runs only with the temporary
/// <c>route-firebird</c> container; other runs exclude it with <c>Category!=SqlE2ETier2</c>.
/// </summary>
[Trait("Category", "SqlE2ETier2")]
[Trait("SqlProvider", "firebird")]
public sealed class FirebirdE2ETests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly FirebirdE2EProvider _provider = new();
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
    public async Task Facts_Batch_Savepoints_RepeatedNamedParameter()
    {
        await using var connection = await Db.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CAST(@x AS INTEGER) * 100 + CAST(@y AS INTEGER) * 10 + CAST(@x AS INTEGER) FROM RDB$DATABASE";
        command.Parameters.Add(new FbParameter("@y", 2));
        command.Parameters.Add(new FbParameter("@x", 1));
        var value = 0;
        var thrown = await Outcome.Of(async () => value = Convert.ToInt32(await command.ExecuteScalarAsync()));

        output.WriteLine($"[fact] firebird {connection.ServerVersion}: CanCreateBatch={connection.CanCreateBatch}, " +
                         $"SupportsSavepoints={transaction.SupportsSavepoints}, repeated @x -> {Outcome.Describe(thrown)} {value}");
        connection.CanCreateBatch.Should().Be(_provider.CanCreateBatch);
        transaction.SupportsSavepoints.Should().Be(_provider.ReportsSavepointSupport);
        thrown.Should().BeNull(Outcome.Describe(thrown));
        value.Should().Be(121, "FirebirdClient binds @name by name, a repeated name included");
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

        output.WriteLine($"[fact] firebird: duplicate key -> {Outcome.Describe(duplicate)} (ErrorCode {(duplicate as FbException)?.ErrorCode})");
        duplicate.Should().BeOfType<FbException>(Outcome.Describe(duplicate)).Which.ErrorCode.Should().Be(335544665, "unique_key_violation");
        (await Db.ReadIdsAsync()).Should().Equal(_provider.LoopContinueDuplicateKeyRows, "the failed statement is undone, the transaction goes on");
    }

    [Fact]
    public async Task Facts_ConversionErrorInTransaction_RollsBackTheStatementOnly()
    {
        Exception? conversion;
        await using (var connection = await Db.OpenAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, _provider.InsertLiteral(Db.Table, 1, "a"));
            conversion = await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, transaction, _provider.TransactionKillingLiteral(Db.Table)));
            await SqlE2EDatabase.ExecuteAsync(connection, transaction, _provider.InsertLiteral(Db.Table, 3, "c"));
            await transaction.CommitAsync();
        }

        output.WriteLine($"[fact] firebird: conversion error -> {Outcome.Describe(conversion)}");
        conversion.Should().BeOfType<FbException>(Outcome.Describe(conversion));
        (await Db.ReadIdsAsync()).Should().Equal(_provider.LoopContinueTransactionKillingRows);
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

    // ── Connector with placeholderStyle=At ──────────────────────────

    [Fact]
    public async Task Connector_Break_DuplicateKey_NoRows()
    {
        var (_, thrown) = await SqlBatchRun.RunAsync(Db, _provider.InsertSql(Db.Table), _provider.DuplicateKeyItems(),
            breakOnError: true, batchSize: 10);

        thrown.Should().BeOfType<FbException>("the provider's exception reaches the route as is: " + Outcome.Describe(thrown));
        thrown!.Data[SqlHeaders.BatchFailedIndex].Should().Be(1, "commands run one by one, so the failed item is known exactly");
        (await Db.ReadIdsAsync()).Should().BeEmpty("a batch that breaks on error rolls everything back");
    }

    [Fact]
    public async Task Connector_Continue_DuplicateKey_OthersCommitted()
    {
        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, _provider.InsertSql(Db.Table), _provider.DuplicateKeyItems(),
            breakOnError: false, batchSize: 10);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Headers[SqlHeaders.BatchStrategy].Should().Be("Savepoints");
        SqlBatchRun.BatchErrorIndexes(exchange).Should().Equal(1);
        (await Db.ReadIdsAsync()).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Connector_Continue_ConversionError_OthersCommitted()
    {
        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, _provider.TransactionKillingBatchSql(Db.Table), _provider.TransactionKillingItems(),
            breakOnError: false, batchSize: 10);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        SqlBatchRun.BatchErrorIndexes(exchange).Should().Equal(1);
        (await Db.ReadIdsAsync()).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Connector_ReturningKeys_CollectedInHeader()
    {
        var items = new List<Dictionary<string, object?>> { new() { ["id"] = 21, ["val"] = "a" }, new() { ["id"] = 22, ["val"] = "b" } };

        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, _provider.ReturningInsertSql(Db.Table), items, breakOnError: null,
            batchSize: 10, new() { ["outputType"] = "SelectList" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Headers.Should().ContainKey(SqlHeaders.GeneratedKeys, "RETURNING rows are collected with outputType=SelectList");
        exchange.In.Headers[SqlHeaders.GeneratedKeys].Should().BeAssignableTo<IEnumerable>().Subject
            .Cast<IDictionary<string, object?>>()
            .Select(row => Convert.ToInt32(row.Values.First()))
            .Should().Equal(21, 22);
    }

    [Fact]
    public async Task Connector_RepeatedPlaceholder_BoundByName()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, Db.CreateConnectionFactory(),
            "SELECT CAST(:#x AS INTEGER) * 100 + CAST(:#y AS INTEGER) * 10 + CAST(:#x AS INTEGER) FROM RDB$DATABASE",
            new() { ["outputType"] = "Scalar" });
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
    public async Task Connector_Poll_OnSuccess_MarksEveryRow()
    {
        for (var id = 1; id <= 3; id++)
            await Db.ExecuteAsync(_provider.InsertLiteral(Db.Table, id, "new"));
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
}
