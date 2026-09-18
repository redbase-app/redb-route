using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// SQL Server through Microsoft.Data.SqlClient, in two session flavours: <c>XACT_ABORT OFF</c> (server default)
/// and <c>XACT_ABORT ON</c>. Container <c>msdb</c>. Each flavour uses its own application name, so pooled
/// connections never carry one flavour's session setting into the other's tests.
/// </summary>
public sealed class MsSqlE2EProvider : SqlE2EProvider
{
    private const string EnvironmentVariable = "ROUTE_SQL_MSSQL_CS";
    private const string DefaultConnectionString =
        "Server=127.0.0.1,1433;Database=tempdb;User Id=sa;Password=1;TrustServerCertificate=true;Connect Timeout=5";

    private readonly bool _xactAbortOn;

    public MsSqlE2EProvider(bool xactAbortOn) => _xactAbortOn = xactAbortOn;

    public override string Name => _xactAbortOn ? "mssql-xact-on" : "mssql";

    public override DbProviderFactory Factory => SqlClientFactory.Instance;

    public override string ConnectionString
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim() is { Length: > 0 } cs ? cs : DefaultConnectionString;
            return new SqlConnectionStringBuilder(raw) { ApplicationName = "rsql-e2e-" + Name }.ConnectionString;
        }
    }

    public override string StartHint =>
        $"Start it with 'docker start msdb' (or 'docker compose -f redb.Route/docker-compose.tests.yml up -d msdb'); " +
        $"the stock compose image uses another sa password — set {EnvironmentVariable}. Use 127.0.0.1, not localhost.";

    public override string SessionInitSql => _xactAbortOn ? "SET XACT_ABORT ON" : "SET XACT_ABORT OFF";

    public override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (id int PRIMARY KEY, val nvarchar(50) NOT NULL)";

    public override string CreateNullableTableSql(string table) =>
        $"CREATE TABLE {table} (id int PRIMARY KEY, val nvarchar(50) NULL, n bigint NULL)";

    public override string ReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) OUTPUT inserted.id VALUES (:#id, :#val)";

    public override string NativeReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) OUTPUT inserted.id VALUES (@id, @val)";

    public override string TransactionKillingLiteral(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (CAST('abc' AS int), 'x')";

    public override string TransactionKillingBatchSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (CAST(:#id AS int), :#val)";

    public override List<Dictionary<string, object?>> TransactionKillingItems() =>
    [
        new() { ["id"] = "1", ["val"] = "a" },
        new() { ["id"] = "abc", ["val"] = "x" },
        new() { ["id"] = "3", ["val"] = "c" },
    ];

    // Conversion error 245 rolls the server transaction back even with XACT_ABORT OFF; with ON any error does.
    // SqlClient then runs the next command in autocommit and only Commit/Save reject the dead transaction.
    public override int[] LoopContinueDuplicateKeyRows => _xactAbortOn ? [3] : [1, 3];
    public override int[] LoopContinueTransactionKillingRows => [3];
    public override bool DuplicateKeyDestroysTransaction => _xactAbortOn;
    public override bool TransactionKillingErrorDestroysTransaction => true;
    public override bool ReportsSavepointSupport => false;
    public override bool CommandAfterTransactionKillingErrorIsRejected => false;
    public override bool CanCreateBatch => true;
    public override bool PrepareRejectsUntypedParameters => true;
    public override bool ReaderClosedOnPrepareLetsScopeCommit => true;
}
