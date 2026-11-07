using System;
using System.Threading;
using System.Threading.Tasks;

namespace redb.Route.Abstractions;

/// <summary>
/// Repository for the Claim Check EIP pattern.
/// Stores opaque binary data associated with a unique key.
/// Implementations may provide TTL-based auto-expiry.
/// </summary>
public interface IClaimCheckRepository
{
    /// <summary>
    /// Stores data under an auto-generated unique key.
    /// </summary>
    /// <param name="data">Binary payload to store.</param>
    /// <param name="ttl">Optional time-to-live. Null uses repository default or no expiry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Unique claim key for later retrieval.</returns>
    Task<string> Store(ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>
    /// Stores data under a specific key. Overwrites if key already exists.
    /// </summary>
    /// <param name="key">Explicit claim key.</param>
    /// <param name="data">Binary payload to store.</param>
    /// <param name="ttl">Optional time-to-live. Null uses repository default or no expiry.</param>
    /// <param name="ct">Cancellation token.</param>
    Task Store(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves data by claim key without removing it.
    /// Returns null if key not found or expired.
    /// </summary>
    Task<byte[]?> Retrieve(string key, CancellationToken ct = default);

    /// <summary>
    /// Retrieves and removes data by claim key atomically.
    /// Returns null if key not found or expired.
    /// </summary>
    Task<byte[]?> RetrieveAndRemove(string key, CancellationToken ct = default);

    /// <summary>
    /// Explicitly removes data by claim key.
    /// No-op if key not found.
    /// </summary>
    Task Remove(string key, CancellationToken ct = default);
}
