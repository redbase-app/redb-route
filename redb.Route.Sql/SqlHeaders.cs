namespace redb.Route.Sql;

/// <summary>
/// Constants for exchange headers set by the SQL component.
/// All prefixed with "redbSql." to avoid collisions with other connectors.
/// </summary>
public static class SqlHeaders
{
    /// <summary>Common prefix for all SQL component headers.</summary>
    public const string Prefix = "redbSql.";

    // ── Query metadata ────────────────────────────────────────────────

    /// <summary>The SQL statement that was executed.</summary>
    public const string Query = "redbSql.query";

    /// <summary>Number of rows affected by INSERT/UPDATE/DELETE.</summary>
    public const string UpdateCount = "redbSql.updateCount";

    /// <summary>Number of rows returned by SELECT.</summary>
    public const string RowCount = "redbSql.rowCount";

    /// <summary>Name of the DataSource used for the query.</summary>
    public const string DataSource = "redbSql.dataSource";

    /// <summary>Output type used for result mapping.</summary>
    public const string OutputType = "redbSql.outputType";

    // ── INSERT keys ───────────────────────────────────────────────────

    /// <summary>Auto-generated keys after INSERT (e.g. identity column values).</summary>
    public const string GeneratedKeys = "redbSql.generatedKeys";

    // ── Error ─────────────────────────────────────────────────────────

    /// <summary>Error message when the query failed (if not thrown).</summary>
    public const string Error = "redbSql.error";

    // ── Transaction ───────────────────────────────────────────────────

    /// <summary>Transaction identifier when <c>transacted=true</c>.</summary>
    public const string TransactionId = "redbSql.transactionId";

    // ── Stored procedure ──────────────────────────────────────────────

    /// <summary>Name of the stored procedure that was called.</summary>
    public const string StoredProcedure = "redbSql.storedProcedure";

    // ── Performance ───────────────────────────────────────────────────

    /// <summary>Query execution time in milliseconds.</summary>
    public const string ExecutionTime = "redbSql.executionTime";
}
