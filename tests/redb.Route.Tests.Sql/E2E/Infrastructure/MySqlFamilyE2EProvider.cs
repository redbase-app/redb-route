using System.Data.Common;
using MySqlConnector;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// MySQL and MariaDB through MySqlConnector (tier 2): the same driver, the same statements and the same measured facts. The
/// connector reaches both with the default <c>placeholderStyle=At</c>.
/// </summary>
public abstract class MySqlFamilyE2EProvider : SqlE2EProvider
{
    public override DbProviderFactory Factory => MySqlConnectorFactory.Instance;

    /// <summary>Environment variable that overrides <see cref="DefaultConnectionString"/>.</summary>
    protected abstract string EnvironmentVariable { get; }

    /// <summary>Connection to the temporary container.</summary>
    protected abstract string DefaultConnectionString { get; }

    public override string ConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim() is { Length: > 0 } cs ? cs : DefaultConnectionString;

    public override string StartHint =>
        $"Tier 2, temporary: 'docker compose -f C:\\Work\\yaml\\{Name}\\docker-compose.yml up -d' (container route-{Name}), " +
        $"and 'down -v' after the run; override the connection with {EnvironmentVariable}.";

    // Strict sql_mode (the default of both servers) turns a text that is not a number into error 1366 instead of a warning.
    public override string TransactionKillingLiteral(string table) =>
        $"INSERT INTO {table} (id, val) VALUES ('abc', 'x')";

    public override string TransactionKillingBatchSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (:#id, :#val)";

    public override List<Dictionary<string, object?>> TransactionKillingItems() =>
    [
        new() { ["id"] = "1", ["val"] = "a" },
        new() { ["id"] = "abc", ["val"] = "x" },
        new() { ["id"] = "3", ["val"] = "c" },
    ];

    // Measured 2026-09-15 (MySqlE2ETests, MariaDbE2ETests): InnoDB rolls back the failed statement only and the transaction
    // goes on; MySqlConnector 2.6 has DbBatch, does not name the failed command of a batch, and reports no savepoint support
    // although Save / Rollback(name) work.
    public override int[] LoopContinueDuplicateKeyRows => [1, 3];
    public override int[] LoopContinueTransactionKillingRows => [1, 3];
    public override bool DuplicateKeyDestroysTransaction => false;
    public override bool TransactionKillingErrorDestroysTransaction => false;
    public override bool ReportsSavepointSupport => false;
    public override bool CommandAfterTransactionKillingErrorIsRejected => false;
    public override bool CanCreateBatch => true;
    public override bool PrepareRejectsUntypedParameters => false;
}
