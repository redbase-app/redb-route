using redb.Route.Abstractions;

namespace redb.Route.File;

/// <summary>
/// In-memory idempotent repository for tracking processed files.
/// Tracks files by a composite key (path + last modified + size) to detect re-processing.
/// Thread-safe via HashSet + lock.
/// </summary>
public sealed class InMemoryFileIdempotentRepository : IIdempotentRepository
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
    /// Generates the default idempotent key for a file.
    /// Format: "{absolutePath}|{lastWriteTimeUtc:O}|{length}"
    /// </summary>
    /// <param name="fileInfo">The file to generate a key for.</param>
    /// <returns>Composite key string.</returns>
    public static string DefaultKey(FileInfo fileInfo)
    {
        ArgumentNullException.ThrowIfNull(fileInfo);
        return $"{fileInfo.FullName}|{fileInfo.LastWriteTimeUtc:O}|{fileInfo.Length}";
    }
}
