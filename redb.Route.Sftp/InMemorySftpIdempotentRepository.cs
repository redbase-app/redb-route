using redb.Route.Abstractions;

namespace redb.Route.Sftp;

/// <summary>
/// In-memory idempotent repository for tracking processed SFTP files.
/// Tracks files by a composite key (remote path + last modified + size) to detect re-processing.
/// Thread-safe via HashSet + lock.
/// </summary>
public sealed class InMemorySftpIdempotentRepository : IIdempotentRepository
{
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Current number of tracked keys.</summary>
    public int Count
    {
        get { lock (_lock) return _keys.Count; }
    }

    /// <inheritdoc />
    public Task<bool> Add(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_keys.Add(key));
        }
    }

    /// <inheritdoc />
    public Task Confirm(string key, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Remove(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _keys.Remove(key);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> Contains(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_keys.Contains(key));
        }
    }

    /// <inheritdoc />
    public Task Clear(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _keys.Clear();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Generates the default idempotent key for a remote SFTP file.
    /// Format: "{remotePath}|{lastModifiedUtc:O}|{length}"
    /// </summary>
    /// <param name="remotePath">Full remote path of the file.</param>
    /// <param name="lastModified">Last modified timestamp.</param>
    /// <param name="length">File size in bytes.</param>
    /// <returns>Composite key string.</returns>
    public static string DefaultKey(string remotePath, DateTimeOffset lastModified, long length)
    {
        return $"{remotePath}|{lastModified:O}|{length}";
    }
}
