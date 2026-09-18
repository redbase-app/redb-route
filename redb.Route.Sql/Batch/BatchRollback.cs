using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace redb.Route.Sql.Batch;

/// <summary>Rolls a failed batch back without letting a failing rollback replace the error that caused it.</summary>
internal static class BatchRollback
{
    /// <summary>
    /// Rolls back with <see cref="CancellationToken.None"/> — a cancelled batch still has to release its transaction. A rollback
    /// that fails (typically: the server already ended the transaction) is logged; the caller's original exception is what
    /// propagates.
    /// </summary>
    internal static async Task RunAsync(DbTransaction transaction, ILogger? logger)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "SQL batch transaction rollback failed; the error that ended the batch is rethrown");
        }
    }
}
