using redb.Core.Data;
using redb.Route.Abstractions;

namespace redb.Route.RedbCore.Transactions;

/// <summary>
/// Adapter that bridges <see cref="IRedbTransaction"/> to the route-pipeline
/// <see cref="ITransactedAction"/> contract used by <c>TransactedProcessor</c> and
/// <c>BeginTransactionProcessor</c>.
/// <para>
/// Allows an explicit redb-level transaction to be enrolled in the standard
/// <c>exchange.Properties["TRANSACT_ACTION"]</c> dictionary so that it commits or rolls back
/// alongside other transports (Kafka, Redis, etc.) via the existing pipeline machinery,
/// instead of forcing route authors to write try/catch blocks around
/// <see cref="IRedbContext.BeginTransactionAsync"/>.
/// </para>
/// <para>
/// On <see cref="Commit"/> the underlying <see cref="IRedbTransaction"/> is committed and disposed;
/// on <see cref="Rollback"/> it is rolled back and disposed. The instance is single-use.
/// </para>
/// <para>
/// Note: <see cref="IRedbTransaction.CommitAsync"/> / <see cref="IRedbTransaction.RollbackAsync"/>
/// do not accept a <see cref="CancellationToken"/>, so the token passed to
/// <see cref="ITransactedAction"/> is intentionally ignored.
/// </para>
/// </summary>
public sealed class RedbTransactedAction : ITransactedAction
{
    private readonly IRedbTransaction _tx;
    private int _completed;

    /// <summary>Creates a new adapter wrapping the given redb transaction.</summary>
    /// <param name="tx">An open redb transaction. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tx"/> is null.</exception>
    public RedbTransactedAction(IRedbTransaction tx)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
    }

    /// <summary>The wrapped redb transaction (for diagnostics / advanced scenarios).</summary>
    public IRedbTransaction Transaction => _tx;

    /// <inheritdoc />
    public async Task Commit(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        try
        {
            if (_tx.IsActive)
                await _tx.CommitAsync().ConfigureAwait(false);
        }
        finally
        {
            await _tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task Rollback(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        try
        {
            if (_tx.IsActive)
                await _tx.RollbackAsync().ConfigureAwait(false);
        }
        finally
        {
            await _tx.DisposeAsync().ConfigureAwait(false);
        }
    }
}
