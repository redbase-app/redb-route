using System.Data.Common;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// Characterization of the driver and server, without the connector: what happens to a multi-row write inside
/// one local transaction when a row fails. The batch design stands on these facts (savepoint per item in
/// continue mode, no command after a failed rollback-to-savepoint, rollback of the whole transaction in break
/// mode). They are green by definition; a red one means the provider changed and the design must be revisited.
/// </summary>
public abstract class ProviderSemanticsTestsBase : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    /// <inheritdoc />
    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(Provider);

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_db is not null)
                await _db.DisposeAsync();
        }
        finally
        {
            (Provider as IDisposable)?.Dispose();
        }
    }

    // ── Errors without savepoints ───────────────────────────────────────────

    [Fact]
    public async Task LoopContinue_DuplicateKey_RowsAfterCommit()
    {
        await RunLoopContinueAsync(Provider.InsertLiteral(Db.Table, 1, "dup"));

        (await Db.ReadIdsAsync()).Should().Equal(Provider.LoopContinueDuplicateKeyRows);
    }

    [Fact]
    public async Task LoopContinue_TransactionKillingError_RowsAfterCommit()
    {
        await RunLoopContinueAsync(Provider.TransactionKillingLiteral(Db.Table));

        (await Db.ReadIdsAsync()).Should().Equal(Provider.LoopContinueTransactionKillingRows);
    }

    [Fact]
    public async Task CommandAfterTransactionKillingError_RejectedOrRunOutsideTransaction()
    {
        await using (var connection = await Db.OpenAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await SqlE2EDatabase.ExecuteAsync(connection, tx, Provider.InsertLiteral(Db.Table, 1, "a"));
            var failing = await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, tx, Provider.TransactionKillingLiteral(Db.Table)));
            failing.Should().NotBeNull();

            var next = await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, tx, Provider.InsertLiteral(Db.Table, 3, "c")));
            if (Provider.CommandAfterTransactionKillingErrorIsRejected)
                next.Should().NotBeNull("the provider refuses statements on a failed transaction");
            else
                next.Should().BeNull("the provider runs the statement although the server transaction is gone");

            // The rollback's own failure is not the fact under test; what survives it is.
            await Outcome.Of(() => tx.RollbackAsync());
        }

        int[] expected = Provider.CommandAfterTransactionKillingErrorIsRejected ? [] : [3];
        (await Db.ReadIdsAsync()).Should().Equal(expected,
            "a statement run after the server rolled the transaction back is autocommitted and survives the rollback");
    }

    // ── Savepoints ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SupportsSavepoints_ReportedVersusActual()
    {
        await using var connection = await Db.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        tx.SupportsSavepoints.Should().Be(Provider.ReportsSavepointSupport);
        var save = await Outcome.Of(() => tx.SaveAsync("rsql_probe"));
        save.Should().BeNull($"savepoints work on every tier-1 provider whatever SupportsSavepoints says ({Outcome.Describe(save)})");
    }

    [Fact]
    public async Task Savepoint_DuplicateKey_OutcomeMatchesProvider() =>
        await AssertSavepointOutcomeAsync(Provider.InsertLiteral(Db.Table, 1, "dup"), Provider.DuplicateKeyDestroysTransaction);

    [Fact]
    public async Task Savepoint_TransactionKillingError_OutcomeMatchesProvider() =>
        await AssertSavepointOutcomeAsync(Provider.TransactionKillingLiteral(Db.Table), Provider.TransactionKillingErrorDestroysTransaction);

    // ── Parameters and commands ─────────────────────────────────────────────

    [Fact]
    public async Task CanCreateBatch_MatchesProvider()
    {
        await using var connection = await Db.OpenAsync();

        connection.CanCreateBatch.Should().Be(Provider.CanCreateBatch);
    }

    [Fact]
    public async Task ReusedCommand_NullsAndMixedClrTypes_Persisted()
    {
        await using var nullable = await SqlE2EDatabase.CreateAsync(Provider, Provider.CreateNullableTableSql);

        await using (var connection = await nullable.OpenAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = $"INSERT INTO {nullable.Table} (id, val, n) VALUES (@id, @val, @n)";
            var id = AddParameter(command, "id");
            var val = AddParameter(command, "val");
            var n = AddParameter(command, "n");
            foreach (var (i, v, k) in new (object, object, object)[] { (1, "a", 1), (2, DBNull.Value, 2L), (3, "ccc", DBNull.Value) })
            {
                id.Value = i;
                val.Value = v;
                n.Value = k;
                await command.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        var rows = await nullable.QueryAsync($"SELECT id, val, n FROM {nullable.Table} ORDER BY id");
        rows.Select(Format).Should().Equal("1|a|1", "2|null|2", "3|ccc|null");
    }

    [Fact]
    public async Task ReusedCommand_PrepareWithoutExplicitTypes_MatchesProvider()
    {
        await using (var connection = await Db.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = Provider.NativeInsertSql(Db.Table);
            var id = AddParameter(command, "id");
            var val = AddParameter(command, "val");
            id.Value = 10;
            val.Value = "a";

            var prepare = await Outcome.Of(() => command.PrepareAsync());
            if (Provider.PrepareRejectsUntypedParameters)
                prepare.Should().BeOfType<InvalidOperationException>("the provider requires explicit parameter types to prepare");
            else
                prepare.Should().BeNull();

            await command.ExecuteNonQueryAsync();
            id.Value = 11;
            val.Value = new string('b', 20);
            await command.ExecuteNonQueryAsync();
        }

        (await Db.ReadIdsAsync()).Should().Equal(10, 11);
    }

    [Fact]
    public async Task ExtraParameterFoundInsideLiteralOrComment_IsIgnoredByProvider()
    {
        await using (var connection = await Db.OpenAsync())
        {
            await InsertWithExtraParameterAsync(connection, $"INSERT INTO {Db.Table} (id, val) VALUES (@id, 'user@example.com')", 20, "example");
            await InsertWithExtraParameterAsync(connection, $"INSERT INTO {Db.Table} (id, val) VALUES (@id, 'x') -- @todo", 21, "todo");
        }

        var rows = await Db.QueryAsync($"SELECT id, val FROM {Db.Table} ORDER BY id");
        rows.Select(Format).Should().Equal("20|user@example.com", "21|x");
    }

    [Fact]
    public async Task ReturningStatement_ReaderReturnsKeyAndRecordsAffected()
    {
        var ids = new List<int>();
        int recordsAffected;
        await using (var connection = await Db.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = Provider.NativeReturningInsertSql(Db.Table);
            AddParameter(command, "id").Value = 40;
            AddParameter(command, "val").Value = "k";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                ids.Add(Convert.ToInt32(reader.GetValue(0)));
            await reader.CloseAsync();
            recordsAffected = reader.RecordsAffected;
        }

        ids.Should().Equal(40);
        recordsAffected.Should().Be(1);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task RunLoopContinueAsync(string failingStatement)
    {
        await using var connection = await Db.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        await SqlE2EDatabase.ExecuteAsync(connection, tx, Provider.InsertLiteral(Db.Table, 1, "a"));
        var failing = await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, tx, failingStatement));
        failing.Should().NotBeNull("the middle statement is built to fail");

        // Exactly what SqlProducer.ProcessBatch does today with breakBatchOnError=false: keep going, then commit.
        await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, tx, Provider.InsertLiteral(Db.Table, 3, "c")));
        await Outcome.Of(() => tx.CommitAsync());
    }

    private async Task AssertSavepointOutcomeAsync(string failingStatement, bool destroysTransaction)
    {
        await using (var connection = await Db.OpenAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await SqlE2EDatabase.ExecuteAsync(connection, tx, Provider.InsertLiteral(Db.Table, 1, "a"));
            await tx.SaveAsync("rsql_item");
            var failing = await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, tx, failingStatement));
            failing.Should().NotBeNull("the statement is built to fail");

            var rollbackToSavepoint = await Outcome.Of(() => tx.RollbackAsync("rsql_item"));
            if (destroysTransaction)
            {
                rollbackToSavepoint.Should().NotBeNull("the error ended the server transaction, so its savepoint is gone");
                // Stop here: a further statement would run outside the transaction on SQL Server.
            }
            else
            {
                rollbackToSavepoint.Should().BeNull($"the savepoint must recover the transaction ({Outcome.Describe(rollbackToSavepoint)})");
                await SqlE2EDatabase.ExecuteAsync(connection, tx, Provider.InsertLiteral(Db.Table, 3, "c"));
                await tx.CommitAsync();
            }
        }

        int[] expected = destroysTransaction ? [] : [1, 3];
        (await Db.ReadIdsAsync()).Should().Equal(expected);
    }

    private static async Task InsertWithExtraParameterAsync(DbConnection connection, string sql, int id, string extraName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "id").Value = id;
        AddParameter(command, extraName).Value = DBNull.Value;
        await command.ExecuteNonQueryAsync();
    }

    private static DbParameter AddParameter(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static string Format(object?[] row) => string.Join("|", row.Select(v => v?.ToString() ?? "null"));
}
