using System.Data.Common;
using Oracle.ManagedDataAccess.Client;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// Oracle Free 23 through ODP.NET (tier 2). Container <c>route-oracle</c>, compose in <c>C:\Work\yaml\oracle</c>, brought up for
/// a run and removed after it. The connector reaches Oracle with <c>placeholderStyle=Colon</c>.
/// </summary>
public sealed class OracleE2EProvider : SqlE2EProvider
{
    private const string EnvironmentVariable = "ROUTE_SQL_ORACLE_CS";
    private const string DefaultConnectionString =
        "User Id=rsql;Password=Rsql2026;Data Source=127.0.0.1:1521/FREEPDB1;Connection Timeout=15";

    public override string Name => "oracle";

    public override DbProviderFactory Factory => OracleClientFactory.Instance;

    public override string ConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim() is { Length: > 0 } cs ? cs : DefaultConnectionString;

    public override string StartHint =>
        "Tier 2, temporary: 'docker compose -f C:\\Work\\yaml\\oracle\\docker-compose.yml up -d' (container route-oracle), " +
        $"and 'down -v' after the run; override the connection with {EnvironmentVariable}.";

    public override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (id NUMBER(10) PRIMARY KEY, val VARCHAR2(50) NOT NULL)";

    public override string CreateNullableTableSql(string table) =>
        $"CREATE TABLE {table} (id NUMBER(10) PRIMARY KEY, val VARCHAR2(50) NULL, n NUMBER(19) NULL)";

    public override string ReturningInsertSql(string table) =>
        throw new NotSupportedException("Oracle returns keys only through RETURNING … INTO OUT parameters, which a batch does not collect.");

    public override string NativeReturningInsertSql(string table) => ReturningInsertSql(table);

    public override string TransactionKillingLiteral(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (TO_NUMBER('abc'), 'x')";

    public override string TransactionKillingBatchSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (TO_NUMBER(:#id), :#val)";

    public override List<Dictionary<string, object?>> TransactionKillingItems() =>
    [
        new() { ["id"] = "1", ["val"] = "a" },
        new() { ["id"] = "abc", ["val"] = "x" },
        new() { ["id"] = "3", ["val"] = "c" },
    ];

    // Measured 2026-09-15 (OracleE2ETests): Oracle rolls back the failed statement only and the transaction goes on; ODP.NET
    // 23.26 reports and supports savepoints, has no DbBatch and binds by position by default.
    public override int[] LoopContinueDuplicateKeyRows => [1, 3];
    public override int[] LoopContinueTransactionKillingRows => [1, 3];
    public override bool DuplicateKeyDestroysTransaction => false;
    public override bool TransactionKillingErrorDestroysTransaction => false;
    public override bool ReportsSavepointSupport => true;
    public override bool CommandAfterTransactionKillingErrorIsRejected => false;
    public override bool CanCreateBatch => false;
    public override bool PrepareRejectsUntypedParameters => false;
}
