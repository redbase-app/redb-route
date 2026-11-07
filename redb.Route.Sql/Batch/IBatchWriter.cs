namespace redb.Route.Sql.Batch;

/// <summary>Writes the items of one batch inside the connection and transaction the batch processor opened.</summary>
internal interface IBatchWriter
{
    /// <summary>Strategy name reported in the <see cref="SqlHeaders.BatchStrategy"/> header.</summary>
    string Strategy { get; }

    /// <summary>
    /// Writes the items. The item that ends the batch is reported by throwing <see cref="BatchItemFailedException"/> with the
    /// provider's exception inside; cancellation propagates as is.
    /// </summary>
    Task<BatchOutcome> WriteAsync(BatchWriteRequest request, CancellationToken ct);
}
