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

    /// <summary>
    /// Rows the statements of a batch returned (<c>INSERT … RETURNING</c>, <c>OUTPUT inserted.*</c>), in item order, when
    /// <c>outputType</c> reads rows: a <c>List&lt;Dictionary&lt;string, object?&gt;&gt;</c>, or <c>List&lt;T&gt;</c> with
    /// <c>outputClass</c> (Apache Camel <c>CamelSqlGeneratedKeyRows</c>). The body is left alone.
    /// </summary>
    public const string GeneratedKeys = "redbSql.generatedKeys";

    /// <summary>Number of rows in <see cref="GeneratedKeys"/> (Apache Camel <c>CamelSqlGeneratedKeysRowCount</c>).</summary>
    public const string GeneratedKeysRowCount = "redbSql.generatedKeysRowCount";

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

    // ── Batch ─────────────────────────────────────────────────────────

    /// <summary>
    /// Batch strategy that ran: <c>None</c> (empty batch source, nothing executed), <c>DbBatch</c> (break on the first error,
    /// chunks of <c>batchSize</c> statements per round trip), <c>Commands</c> (break on the first error, the connection has no
    /// <c>DbBatch</c>) or <c>Savepoints</c> (continue past errors). The driver's capability decides, not an option.
    /// </summary>
    public const string BatchStrategy = "redbSql.batchStrategy";

    /// <summary>Round trips of a <c>DbBatch</c> batch (<see cref="BatchStrategy"/> <c>DbBatch</c>); absent for other strategies.</summary>
    public const string BatchChunkCount = "redbSql.batchChunkCount";

    /// <summary>Batch items written successfully; in batch mode <see cref="RowCount"/> carries the same value.</summary>
    public const string BatchItemCount = "redbSql.batchItemCount";

    /// <summary>
    /// Zero-based index of the item that ended a batch. The thrown provider exception carries the same value in its
    /// <see cref="Exception.Data"/> under this key.
    /// </summary>
    public const string BatchFailedIndex = "redbSql.batchFailedIndex";

    /// <summary>
    /// Items that failed and were undone to their savepoint in a batch that continues past errors
    /// (<c>breakBatchOnError=false</c>): an <c>IReadOnlyList&lt;SqlBatchItemError&gt;</c>.
    /// </summary>
    public const string BatchErrors = "redbSql.batchErrors";
}
