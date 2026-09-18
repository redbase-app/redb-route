using System.Data.Common;
using Microsoft.Extensions.Logging;
using redb.Route.Sql.Mapping;

namespace redb.Route.Sql.Batch;

/// <summary>
/// Continue-past-errors batch (<c>breakBatchOnError=false</c>). Every item runs under its own savepoint: a failed item is
/// rolled back to it and reported, the others stay. Whether savepoints work is learnt by using them — providers under-report
/// <see cref="DbTransaction.SupportsSavepoints"/> (SqlClient says false and supports them). When rolling back to the
/// savepoint fails, the server has already ended the transaction; SQL Server would then run the next statement in
/// autocommit, so the batch stops at that item instead of sending anything more. The items share one command whose
/// parameter values are replaced per item; a <c>DbBatch</c> is never used here, since it cannot go on past a failed command.
/// A failure of the source itself is not an item error: it ends the batch. Rows a failed item returned are dropped with it.
/// </summary>
internal sealed class SavepointBatchWriter(SqlEndpointOptions options, ILogger? logger) : IBatchWriter
{
    /// <summary>Name of the per-item savepoint.</summary>
    internal const string SavepointName = "redb_batch_item";

    /// <inheritdoc />
    public string Strategy => "Savepoints";

    /// <inheritdoc />
    public async Task<BatchOutcome> WriteAsync(BatchWriteRequest request, CancellationToken ct)
    {
        var transaction = request.Transaction
            ?? throw new InvalidOperationException("Continuing past a failed batch item needs a local transaction.");
        var outcome = new BatchOutcome();
        await using var command = new BatchItemCommand(request, options);

        while (await request.Items.ReadAsync().ConfigureAwait(false))
        {
            var index = request.Items.Index;
            await SaveAsync(request, transaction, ct).ConfigureAwait(false);
            var keysBefore = request.Keys?.Count ?? 0;

            int affected;
            try
            {
                affected = await command.ExecuteAsync(request.Items.Current, index, ct).ConfigureAwait(false);
            }
            // A returned row that cannot be mapped is the endpoint's configuration, not this item's failure: it goes up as is.
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested && ex is not SqlRowMappingException)
            {
                if (!await TryRollbackToSavepointAsync(transaction, index).ConfigureAwait(false))
                    throw new BatchItemFailedException(index, ex);

                request.Keys?.Truncate(keysBefore);
                outcome.Errors.Add(new SqlBatchItemError(index, ex.Message, (ex as DbException)?.SqlState));
                continue;
            }

            await transaction.ReleaseAsync(SavepointName, ct).ConfigureAwait(false);
            outcome.RowsAffected += affected;
            outcome.ItemsWritten++;
        }

        return outcome;
    }

    private static async Task SaveAsync(BatchWriteRequest request, DbTransaction transaction, CancellationToken ct)
    {
        try
        {
            await transaction.SaveAsync(SavepointName, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException(
                $"breakBatchOnError=false undoes a failed item to a savepoint, and {request.Connection.GetType().Name} does not " +
                "support savepoints. Use breakBatchOnError=true: the whole batch then rolls back on the first error.", ex);
        }
    }

    private async Task<bool> TryRollbackToSavepointAsync(DbTransaction transaction, int index)
    {
        try
        {
            await transaction.RollbackAsync(SavepointName, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Not swallowed: the caller stops the batch and rethrows the item's own error; this records why.
            logger?.LogWarning(ex,
                "SQL batch item {Index} failed and its savepoint can no longer be rolled back to; the transaction has ended, the batch stops",
                index);
            return false;
        }
    }
}
