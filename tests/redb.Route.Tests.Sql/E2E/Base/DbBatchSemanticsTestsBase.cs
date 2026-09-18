using System.Data.Common;
using System.Transactions;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// Characterization of <see cref="DbBatch"/> on providers that implement it: the failed command is identified,
/// a failed batch rolled back leaves nothing (including under an ambient <see cref="TransactionScope"/>), parameter
/// values need no explicit types, and statements returning rows deliver every result set.
/// </summary>
public abstract class DbBatchSemanticsTestsBase : IAsyncLifetime
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

    [Fact]
    public async Task DbBatch_DuplicateKey_ReportsFailedCommandIndex()
    {
        await using var connection = await Db.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var batch = CreateBatch(connection, tx, DuplicateKeyStatements());

        var error = await Outcome.Of(() => batch.ExecuteNonQueryAsync());

        var dbError = error.Should().BeAssignableTo<DbException>().Which;
        dbError.BatchCommand.Should().NotBeNull("the provider identifies the failed command");
        batch.BatchCommands.IndexOf(dbError.BatchCommand!).Should().Be(1);
    }

    [Fact]
    public async Task DbBatch_DuplicateKeyThenRollback_LeavesNoRows() =>
        await AssertFailedBatchRollbackLeavesNoRowsAsync(DuplicateKeyStatements());

    [Fact]
    public async Task DbBatch_TransactionKillingErrorThenRollback_LeavesNoRows() =>
        await AssertFailedBatchRollbackLeavesNoRowsAsync(
            Provider.InsertLiteral(Db.Table, 1, "a"),
            Provider.TransactionKillingLiteral(Db.Table),
            Provider.InsertLiteral(Db.Table, 3, "c"));

    [Fact]
    public async Task DbBatchUnderTransactionScope_ErrorWithoutComplete_LeavesNoRows()
    {
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var connection = await Db.OpenAsync();
            await using var batch = CreateBatch(connection, null, DuplicateKeyStatements());

            var error = await Outcome.Of(() => batch.ExecuteNonQueryAsync());
            error.Should().BeAssignableTo<DbException>();
        }

        (await Db.ReadIdsAsync()).Should().BeEmpty("the scope was not completed");
    }

    [Fact]
    public async Task DbBatch_NullsAndMixedClrTypes_Persisted()
    {
        await using var nullable = await SqlE2EDatabase.CreateAsync(Provider, Provider.CreateNullableTableSql);

        await using (var connection = await nullable.OpenAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        await using (var batch = connection.CreateBatch())
        {
            batch.Transaction = tx;
            foreach (var (id, val, n) in new (object, object, object)[] { (1, "a", 1), (2, DBNull.Value, 2L), (3, "ccc", DBNull.Value) })
            {
                var command = batch.CreateBatchCommand();
                command.CommandText = $"INSERT INTO {nullable.Table} (id, val, n) VALUES (@id, @val, @n)";
                command.CanCreateParameter.Should().BeTrue();
                AddParameter(command, "id", id);
                AddParameter(command, "val", val);
                AddParameter(command, "n", n);
                batch.BatchCommands.Add(command);
            }

            (await batch.ExecuteNonQueryAsync()).Should().Be(3);
            await tx.CommitAsync();
        }

        var rows = await nullable.QueryAsync($"SELECT id, val, n FROM {nullable.Table} ORDER BY id");
        rows.Select(r => string.Join("|", r.Select(v => v?.ToString() ?? "null")))
            .Should().Equal("1|a|1", "2|null|2", "3|ccc|null");
    }

    [Fact]
    public async Task DbBatch_ReturningStatements_KeysFromEveryResultAndRecordsAffectedPerCommand()
    {
        var ids = new List<int>();
        int[] recordsAffected;
        await using (var connection = await Db.OpenAsync())
        await using (var batch = connection.CreateBatch())
        {
            foreach (var id in new[] { 30, 31 })
            {
                var command = batch.CreateBatchCommand();
                command.CommandText = Provider.NativeReturningInsertSql(Db.Table);
                AddParameter(command, "id", id);
                AddParameter(command, "val", "k");
                batch.BatchCommands.Add(command);
            }

            await using (var reader = await batch.ExecuteReaderAsync())
            {
                do
                {
                    while (await reader.ReadAsync())
                        ids.Add(Convert.ToInt32(reader.GetValue(0)));
                }
                while (await reader.NextResultAsync());
            }

            recordsAffected = batch.BatchCommands.Select(c => c.RecordsAffected).ToArray();
        }

        ids.Should().Equal(30, 31);
        recordsAffected.Should().Equal(1, 1);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private string[] DuplicateKeyStatements() =>
    [
        Provider.InsertLiteral(Db.Table, 1, "a"),
        Provider.InsertLiteral(Db.Table, 1, "dup"),
        Provider.InsertLiteral(Db.Table, 3, "c"),
    ];

    private async Task AssertFailedBatchRollbackLeavesNoRowsAsync(params string[] statements)
    {
        await using (var connection = await Db.OpenAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        await using (var batch = CreateBatch(connection, tx, statements))
        {
            var error = await Outcome.Of(() => batch.ExecuteNonQueryAsync());
            error.Should().BeAssignableTo<DbException>();

            var rollback = await Outcome.Of(() => tx.RollbackAsync());
            rollback.Should().BeNull($"rolling back a failed batch works on this provider ({Outcome.Describe(rollback)})");
        }

        (await Db.ReadIdsAsync()).Should().BeEmpty("no command of a failed, rolled-back batch may survive");
    }

    private static DbBatch CreateBatch(DbConnection connection, DbTransaction? transaction, params string[] statements)
    {
        var batch = connection.CreateBatch();
        batch.Transaction = transaction;
        foreach (var statement in statements)
        {
            var command = batch.CreateBatchCommand();
            command.CommandText = statement;
            batch.BatchCommands.Add(command);
        }
        return batch;
    }

    private static void AddParameter(DbBatchCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
