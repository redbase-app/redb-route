using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// SQLite through Microsoft.Data.Sqlite on a temporary file (not shared memory), so the connector's own
/// connections and the verifying reader see the same database the way a deployment would. Pooling is off:
/// the file is deleted on dispose.
/// </summary>
public sealed class SqliteFileE2EProvider : SqlE2EProvider, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rsql-e2e-{Guid.NewGuid():N}.db");

    public override string Name => "sqlite-file";

    public override DbProviderFactory Factory => SqliteFactory.Instance;

    public override string ConnectionString => $"Data Source={_path};Pooling=False";

    public override string StartHint => $"The database is a temporary file ({_path}); nothing to start.";

    public override string ReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (:#id, :#val) RETURNING id";

    public override string NativeReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (@id, @val) RETURNING id";

    public override string TransactionKillingLiteral(string table) =>
        $"INSERT OR ROLLBACK INTO {table} (id, val) VALUES (1, 'x')";

    public override string TransactionKillingBatchSql(string table) =>
        $"INSERT OR ROLLBACK INTO {table} (id, val) VALUES (:#id, :#val)";

    public override List<Dictionary<string, object?>> TransactionKillingItems() =>
    [
        new() { ["id"] = 1, ["val"] = "a" },
        new() { ["id"] = 1, ["val"] = "x" },
        new() { ["id"] = 3, ["val"] = "c" },
    ];

    // Constraint errors abort the statement only; OR ROLLBACK ends the transaction and the driver then
    // refuses every further command on it.
    public override int[] LoopContinueDuplicateKeyRows => [1, 3];
    public override int[] LoopContinueTransactionKillingRows => [];
    public override bool DuplicateKeyDestroysTransaction => false;
    public override bool TransactionKillingErrorDestroysTransaction => true;
    public override bool ReportsSavepointSupport => true;
    public override bool CommandAfterTransactionKillingErrorIsRejected => true;
    public override bool CanCreateBatch => false;
    public override bool PrepareRejectsUntypedParameters => false;
    public override bool ReaderConnectionRunsAnotherCommand => true;

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
