using System.Data.Common;
using Npgsql;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>PostgreSQL through Npgsql. Container <c>pgdb</c> (compose in <c>C:\Work\yaml\postgres</c>).</summary>
public sealed class PostgresE2EProvider : SqlE2EProvider
{
    private const string EnvironmentVariable = "ROUTE_SQL_PG_CS";
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=1;Database=postgres;Timeout=5";

    public override string Name => "postgres";

    public override DbProviderFactory Factory => NpgsqlFactory.Instance;

    public override string ConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim() is { Length: > 0 } cs ? cs : DefaultConnectionString;

    public override string StartHint =>
        $"Start it with 'docker start pgdb' (or 'docker compose -f redb.Route/docker-compose.tests.yml up -d pgdb'); override the connection with {EnvironmentVariable}.";

    public override string ReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (:#id, :#val) RETURNING id";

    public override string NativeReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (@id, @val) RETURNING id";

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

    // Any error aborts the transaction block; COMMIT then answers ROLLBACK and Npgsql does not raise.
    public override int[] LoopContinueDuplicateKeyRows => [];
    public override int[] LoopContinueTransactionKillingRows => [];
    public override bool DuplicateKeyDestroysTransaction => false;
    public override bool TransactionKillingErrorDestroysTransaction => false;
    public override bool ReportsSavepointSupport => true;
    public override bool CommandAfterTransactionKillingErrorIsRejected => true;
    public override bool CanCreateBatch => true;
    public override bool PrepareRejectsUntypedParameters => false;
}
