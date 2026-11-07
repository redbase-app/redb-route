namespace redb.Route.Abstractions;

/// <summary>
/// Represents a transacted action that can be committed or rolled back.
/// Used for deferred-commit transaction pattern across brokers.
/// </summary>
public interface ITransactedAction
{
    /// <summary>Commits the transaction.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task Commit(CancellationToken ct = default);

    /// <summary>Rolls back the transaction.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task Rollback(CancellationToken ct = default);
}
