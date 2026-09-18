using System.Data.Common;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// One database the SQL E2E suites run against: how to reach it, the statements each scenario needs, and
/// the driver/server facts the characterization suite pins down. The expectations are facts measured on
/// real servers, not wishes: when a driver upgrade changes one of them, the characterization suite fails
/// first and the batch design is revisited before any product code relies on the old fact.
/// </summary>
/// <remarks>
/// "Transaction-killing error" means the error class that rolls the whole server transaction back on at
/// least one provider (SQL Server conversion error 245, SQLite <c>INSERT OR ROLLBACK</c>). PostgreSQL gets a
/// conversion error too, for symmetry: there any error aborts the transaction block, yet a savepoint still
/// recovers it.
/// </remarks>
public abstract class SqlE2EProvider
{
    /// <summary>Trait value (<c>SqlProvider=...</c>) and the name used in messages.</summary>
    public abstract string Name { get; }

    /// <summary>ADO.NET factory; the connector gets it through a registered connection factory.</summary>
    public abstract DbProviderFactory Factory { get; }

    /// <summary>Connection string, environment override first.</summary>
    public abstract string ConnectionString { get; }

    /// <summary>How to bring the server up when it is not reachable.</summary>
    public abstract string StartHint { get; }

    /// <summary>SQL run on every opened connection (session settings), or null.</summary>
    public virtual string? SessionInitSql => null;

    // ── Statements ───────────────────────────────────────────────────────────

    /// <summary>A two-column table: integer primary key and a non-null text value.</summary>
    public virtual string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (id int PRIMARY KEY, val varchar(50) NOT NULL)";

    /// <summary>Like <see cref="CreateTableSql"/>, but <c>val</c> and a <c>bigint</c> column <c>n</c> are nullable.</summary>
    public virtual string CreateNullableTableSql(string table) =>
        $"CREATE TABLE {table} (id int PRIMARY KEY, val varchar(50) NULL, n bigint NULL)";

    /// <summary>Insert through the connector (<c>:#name</c>) that returns the inserted id as a result set (<c>RETURNING</c> / <c>OUTPUT</c>).</summary>
    public abstract string ReturningInsertSql(string table);

    /// <summary><see cref="ReturningInsertSql"/> in the driver's own syntax (<c>@name</c>), for ADO.NET commands built directly.</summary>
    public abstract string NativeReturningInsertSql(string table);

    /// <summary>Literal insert of one row.</summary>
    public string InsertLiteral(string table, int id, string val) =>
        $"INSERT INTO {table} (id, val) VALUES ({id}, '{val}')";

    /// <summary>Parameterized insert the connector binds per batch item.</summary>
    public string InsertSql(string table) => $"INSERT INTO {table} (id, val) VALUES (:#id, :#val)";

    /// <summary><see cref="InsertSql"/> in the driver's own syntax (<c>@name</c>), for ADO.NET commands built directly.</summary>
    public string NativeInsertSql(string table) => $"INSERT INTO {table} (id, val) VALUES (@id, @val)";

    /// <summary>Literal statement that raises the transaction-killing error after row 1 exists.</summary>
    public abstract string TransactionKillingLiteral(string table);

    /// <summary>Parameterized statement whose middle item of <see cref="TransactionKillingItems"/> fails.</summary>
    public abstract string TransactionKillingBatchSql(string table);

    /// <summary>Three items for <see cref="TransactionKillingBatchSql"/>: ids 1 and 3 succeed, the middle one fails.</summary>
    public abstract List<Dictionary<string, object?>> TransactionKillingItems();

    /// <summary>Three items for <see cref="InsertSql"/>: ids 1 and 3 succeed, the middle one repeats id 1.</summary>
    public List<Dictionary<string, object?>> DuplicateKeyItems() =>
    [
        new() { ["id"] = 1, ["val"] = "a" },
        new() { ["id"] = 1, ["val"] = "dup" },
        new() { ["id"] = 3, ["val"] = "c" },
    ];

    // ── Measured facts (characterization expectations) ──────────────────────

    /// <summary>Rows left after: insert 1, duplicate key, insert 3, commit — errors ignored, no savepoints.</summary>
    public abstract int[] LoopContinueDuplicateKeyRows { get; }

    /// <summary>Rows left after: insert 1, transaction-killing error, insert 3, commit attempt.</summary>
    public abstract int[] LoopContinueTransactionKillingRows { get; }

    /// <summary>After a duplicate key inside a savepoint, rolling back to that savepoint fails.</summary>
    public abstract bool DuplicateKeyDestroysTransaction { get; }

    /// <summary>After the transaction-killing error inside a savepoint, rolling back to that savepoint fails.</summary>
    public abstract bool TransactionKillingErrorDestroysTransaction { get; }

    /// <summary>What <see cref="DbTransaction.SupportsSavepoints"/> reports (not whether savepoints work).</summary>
    public abstract bool ReportsSavepointSupport { get; }

    /// <summary>
    /// The statement sent right after the transaction-killing error is refused by the driver or server.
    /// False means it runs — on SQL Server outside any transaction, in autocommit.
    /// </summary>
    public abstract bool CommandAfterTransactionKillingErrorIsRejected { get; }

    /// <summary>What <see cref="DbConnection.CanCreateBatch"/> reports.</summary>
    public abstract bool CanCreateBatch { get; }

    /// <summary><see cref="DbCommand.Prepare"/> refuses parameters whose type was never set explicitly.</summary>
    public abstract bool PrepareRejectsUntypedParameters { get; }

    /// <summary>
    /// A reader left open in a <see cref="System.Transactions.TransactionScope"/> and closed by a volatile enlistment on
    /// Prepare lets the scope commit. False where the driver commits before volatile enlistments are prepared.
    /// </summary>
    public virtual bool ReaderClosedOnPrepareLetsScopeCommit => false;

    /// <summary>
    /// The connection runs another statement while one of its readers is still open. False where the driver refuses it
    /// ("a command is already in progress", "already an open DataReader").
    /// </summary>
    public virtual bool ReaderConnectionRunsAnotherCommand => false;
}
