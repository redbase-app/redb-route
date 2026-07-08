namespace redb.Route.Abstractions;

/// <summary>
/// Repository that tracks message identifiers for idempotent consumption.
/// Implementations may use in-memory stores, Redis, or a database.
/// </summary>
public interface IIdempotentRepository
{
    /// <summary>
    /// Attempts to add the key. Returns true if the key was new (not a duplicate).
    /// Returns false if the key already exists (duplicate).
    /// </summary>
    /// <param name="key">Unique message identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the message should be processed; false if it is a duplicate.</returns>
    Task<bool> Add(string key, CancellationToken ct = default);

    /// <summary>
    /// Confirms the key after successful processing.
    /// For implementations that use two-phase commit (add → process → confirm).
    /// </summary>
    /// <param name="key">Unique message identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task Confirm(string key, CancellationToken ct = default);

    /// <summary>
    /// Removes the key, allowing it to be reprocessed.
    /// Called on rollback or when retrying.
    /// </summary>
    /// <param name="key">Unique message identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task Remove(string key, CancellationToken ct = default);

    /// <summary>
    /// Checks if the key already exists in the repository.
    /// </summary>
    /// <param name="key">Unique message identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the key exists.</returns>
    Task<bool> Contains(string key, CancellationToken ct = default);

    /// <summary>
    /// Removes all keys from the repository.
    /// Primarily for testing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task Clear(CancellationToken ct = default);
}
