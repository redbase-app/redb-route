using redb.Route.Abstractions;

namespace redb.Route.GenericFile;

/// <summary>
/// In-memory idempotent repository for tracking processed files across all file-based transports.
/// Tracks files by a composite key (path + last modified + size) to detect re-processing.
/// Thread-safe via HashSet + lock.
/// </summary>
public sealed class InMemoryGenericFileIdempotentRepository : IIdempotentRepository
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
    /// Format: "{fullPath}|{lastModifiedUtc:O}|{length}"
    /// </summary>
    public static string DefaultKey(GenericFileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return $"{file.FullPath}|{file.LastModified:O}|{file.Length}";
    }
}
