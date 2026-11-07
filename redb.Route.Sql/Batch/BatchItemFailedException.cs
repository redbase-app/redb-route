namespace redb.Route.Sql.Batch;

/// <summary>
/// Internal carrier from a batch writer to the batch processor: the item at <see cref="Index"/> ended the batch. It never
/// leaves the connector — the processor rolls back and rethrows the provider's exception, which is the inner exception.
/// </summary>
internal sealed class BatchItemFailedException(int index, Exception providerException)
    : Exception($"Batch item {index} failed.", providerException)
{
    /// <summary>Zero-based position of the failed item in the batch source.</summary>
    public int Index { get; } = index;
}
