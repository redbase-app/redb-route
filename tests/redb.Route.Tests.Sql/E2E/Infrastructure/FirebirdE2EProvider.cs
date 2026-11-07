using System.Data.Common;
using FirebirdSql.Data.FirebirdClient;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// Firebird 5 through FirebirdClient (tier 2). Container <c>route-firebird</c>, compose in <c>C:\Work\yaml\firebird</c>,
/// brought up for a run and removed after it. The connector reaches Firebird with the default <c>placeholderStyle=At</c>.
/// </summary>
/// <remarks>
/// Pooling is off: a pooled attachment can keep the table in use and have the <c>DROP TABLE</c> of the test's dispose refused.
/// </remarks>
public sealed class FirebirdE2EProvider : SqlE2EProvider
{
    private const string EnvironmentVariable = "ROUTE_SQL_FIREBIRD_CS";
    private const string DefaultConnectionString =
        "User=SYSDBA;Password=1;Database=/var/lib/firebird/data/route_sql.fdb;DataSource=127.0.0.1;Port=3050;Charset=UTF8;" +
        "Pooling=false;Connection Timeout=15";

    public override string Name => "firebird";

    public override DbProviderFactory Factory => FirebirdClientFactory.Instance;

    public override string ConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim() is { Length: > 0 } cs ? cs : DefaultConnectionString;

    public override string StartHint =>
        "Tier 2, temporary: 'docker compose -f C:\\Work\\yaml\\firebird\\docker-compose.yml up -d' (container route-firebird), " +
        $"and 'down -v' after the run; override the connection with {EnvironmentVariable}.";

    public override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (id INTEGER NOT NULL PRIMARY KEY, val VARCHAR(50) NOT NULL)";

    public override string CreateNullableTableSql(string table) =>
        $"CREATE TABLE {table} (id INTEGER NOT NULL PRIMARY KEY, val VARCHAR(50), n BIGINT)";

    public override string ReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (:#id, :#val) RETURNING id";

    public override string NativeReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (@id, @val) RETURNING id";

    public override string TransactionKillingLiteral(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (CAST('abc' AS INTEGER), 'x')";

    public override string TransactionKillingBatchSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (CAST(:#id AS INTEGER), :#val)";

    public override List<Dictionary<string, object?>> TransactionKillingItems() =>
    [
        new() { ["id"] = "1", ["val"] = "a" },
        new() { ["id"] = "abc", ["val"] = "x" },
        new() { ["id"] = "3", ["val"] = "c" },
    ];

    // Measured 2026-09-15 (FirebirdE2ETests): every statement runs under its own implicit savepoint, so a failed one is undone
    // and the transaction goes on.
    public override int[] LoopContinueDuplicateKeyRows => [1, 3];
    public override int[] LoopContinueTransactionKillingRows => [1, 3];
    public override bool DuplicateKeyDestroysTransaction => false;
    public override bool TransactionKillingErrorDestroysTransaction => false;
    public override bool ReportsSavepointSupport => true;
    public override bool CommandAfterTransactionKillingErrorIsRejected => false;
    public override bool CanCreateBatch => false;
    public override bool PrepareRejectsUntypedParameters => false;
}
