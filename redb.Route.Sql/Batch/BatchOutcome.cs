namespace redb.Route.Sql.Batch;

/// <summary>What a batch writer achieved: rows the statements reported, items written, round trips, and items undone to a savepoint.</summary>
internal sealed class BatchOutcome
{
    /// <summary>Sum of the rows affected reported by the provider.</summary>
    public int RowsAffected { get; set; }

    /// <summary>Items whose statement succeeded and stays in the batch.</summary>
    public int ItemsWritten { get; set; }

    /// <summary><c>DbBatch</c> round trips; null for strategies that send one statement at a time.</summary>
    public int? ChunkCount { get; set; }

    /// <summary>Items that failed and were undone to their savepoint (continue mode only).</summary>
    public List<SqlBatchItemError> Errors { get; } = [];
}
