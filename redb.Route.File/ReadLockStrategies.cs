namespace redb.Route.File;

/// <summary>
/// Abstraction for read lock strategies used by the file consumer.
/// Each strategy determines how to acquire and release locks on files being polled,
/// preventing the consumer from reading a file that is still being written.
/// </summary>
public interface IReadLockStrategy
{
    /// <summary>
    /// Attempts to acquire a read lock on the file.
    /// </summary>
    /// <param name="file">The file to lock.</param>
    /// <param name="options">Endpoint options with lock configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the lock was acquired; false if the file should be skipped.</returns>
    Task<bool> AcquireLock(FileInfo file, FileEndpointOptions options, CancellationToken ct);

    /// <summary>
    /// Releases the read lock on the file after processing.
    /// </summary>
    /// <param name="file">The file to unlock.</param>
    /// <param name="options">Endpoint options with lock configuration.</param>
    void ReleaseLock(FileInfo file, FileEndpointOptions options);

    /// <summary>
    /// Path the file occupies while this lock is held. Strategies that relocate the file
    /// (see <see cref="ReadLockStrategy.Rename"/>) report the new location here so the
    /// consumer reads and post-processes the right path.
    /// </summary>
    /// <param name="originalPath">Path the file had before the lock was acquired.</param>
    /// <returns>The effective path. Default: unchanged.</returns>
    string GetWorkPath(string originalPath) => originalPath;

    /// <summary>
    /// Read stream already held by this lock, if any. Strategies that keep an exclusive
    /// handle (see <see cref="ReadLockStrategy.FileLock"/>) expose it here: reopening such a
    /// file would fail against the lock this very consumer holds.
    /// </summary>
    /// <param name="originalPath">Path the file had before the lock was acquired.</param>
    /// <returns>The open stream, or null when the consumer should open the file itself.</returns>
    Stream? GetLockedStream(string originalPath) => null;
}

/// <summary>
/// No read lock — file is picked up immediately.
/// </summary>
internal sealed class NoReadLock : IReadLockStrategy
{
    public static readonly NoReadLock Instance = new();

    public Task<bool> AcquireLock(FileInfo file, FileEndpointOptions options, CancellationToken ct)
        => Task.FromResult(true);

    public void ReleaseLock(FileInfo file, FileEndpointOptions options) { }
}

/// <summary>
/// Marker file lock strategy. Creates a *.redbLock file next to the target.
/// Other consumers skip files with an existing lock marker.
/// </summary>
internal sealed class MarkerFileReadLock : IReadLockStrategy
{
    public static readonly MarkerFileReadLock Instance = new();

    public Task<bool> AcquireLock(FileInfo file, FileEndpointOptions options, CancellationToken ct)
    {
        var markerPath = file.FullName + options.ReadLockMarkerFileExtension;

        if (System.IO.File.Exists(markerPath))
            return Task.FromResult(false); // Another consumer holds the lock

        try
        {
            // Create marker file atomically — if it already exists, FileMode.CreateNew throws
            using var fs = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            // Race condition: another process created the marker first
            return Task.FromResult(false);
        }
    }

    public void ReleaseLock(FileInfo file, FileEndpointOptions options)
    {
        var markerPath = file.FullName + options.ReadLockMarkerFileExtension;
        try
        {
            System.IO.File.Delete(markerPath);
        }
        catch (IOException)
        {
            // Best effort
        }
    }
}

/// <summary>
/// Changed-size read lock. Waits until the file size stops changing for
/// <see cref="FileEndpointOptions.ReadLockMinAge"/> milliseconds.
/// Detects files still being written by monitoring size stability.
/// </summary>
internal sealed class ChangedReadLock : IReadLockStrategy
{
    public static readonly ChangedReadLock Instance = new();

    public async Task<bool> AcquireLock(FileInfo file, FileEndpointOptions options, CancellationToken ct)
    {
        var timeout = options.ReadLockTimeout;
        var checkInterval = options.ReadLockCheckInterval;
        var minAge = options.ReadLockMinAge;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var lastSize = file.Length;
        var lastModified = file.LastWriteTimeUtc;
        var stableStart = sw.ElapsedMilliseconds;

        while (sw.ElapsedMilliseconds < timeout)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(checkInterval, ct).ConfigureAwait(false);

            file.Refresh();
            if (!file.Exists)
                return false; // File was deleted while waiting

            if (file.Length != lastSize || file.LastWriteTimeUtc != lastModified)
            {
                // File changed — reset stability timer
                lastSize = file.Length;
                lastModified = file.LastWriteTimeUtc;
                stableStart = sw.ElapsedMilliseconds;
            }
            else if (sw.ElapsedMilliseconds - stableStart >= minAge)
            {
                // File has been stable for minAge
                return true;
            }
        }

        return false; // Timeout
    }

    public void ReleaseLock(FileInfo file, FileEndpointOptions options) { }
}

/// <summary>
/// OS-level file lock using FileShare.None. If the file cannot be opened exclusively,
/// it means another process has it locked.
/// </summary>
internal sealed class FileLockReadLock : IReadLockStrategy
{
    // Holds active lock streams keyed by absolute path
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileStream> _locks = new();

    public Task<bool> AcquireLock(FileInfo file, FileEndpointOptions options, CancellationToken ct)
    {
        try
        {
            // FileShare.Delete and nothing else: other processes can neither read nor write the
            // file while we hold it, but this process can still delete or move it during
            // post-processing (Windows requires FILE_SHARE_DELETE on the open handle for that).
            var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Delete);
            _locks[file.FullName] = fs;
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            return Task.FromResult(false); // File is locked by another process
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    public void ReleaseLock(FileInfo file, FileEndpointOptions options)
    {
        if (_locks.TryRemove(file.FullName, out var fs))
        {
            fs.Dispose();
        }
    }

    /// <summary>
    /// The exclusive handle acquired by this strategy. The consumer must read through it:
    /// opening the file a second time would be denied by our own lock.
    /// </summary>
    public Stream? GetLockedStream(string originalPath)
        => _locks.TryGetValue(originalPath, out var fs) ? fs : null;
}

/// <summary>
/// Rename lock strategy. Tries to rename the file to a temp name; if rename fails,
/// the file is locked by another process. Renames back after processing.
/// </summary>
internal sealed class RenameReadLock : IReadLockStrategy
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _renames = new();

    /// <summary>
    /// While the lock is held the file lives under the temporary name, so that is where the
    /// consumer must read from and post-process.
    /// </summary>
    public string GetWorkPath(string originalPath)
        => _renames.TryGetValue(originalPath, out var temp) ? temp : originalPath;

    public Task<bool> AcquireLock(FileInfo file, FileEndpointOptions options, CancellationToken ct)
    {
        var tempName = file.FullName + ".redbRename";
        try
        {
            System.IO.File.Move(file.FullName, tempName);
            _renames[file.FullName] = tempName;
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            return Task.FromResult(false); // File in use
        }
    }

    public void ReleaseLock(FileInfo file, FileEndpointOptions options)
    {
        if (_renames.TryRemove(file.FullName, out var tempName))
        {
            try
            {
                if (System.IO.File.Exists(tempName))
                {
                    System.IO.File.Move(tempName, file.FullName);
                }
            }
            catch (IOException)
            {
                // Best effort — file may have been moved/deleted during processing
            }
        }
    }
}

/// <summary>
/// Factory for creating read lock strategy instances based on configuration.
/// </summary>
internal static class ReadLockFactory
{
    /// <summary>Creates the appropriate read lock strategy for the given configuration.</summary>
    public static IReadLockStrategy Create(ReadLockStrategy strategy) => strategy switch
    {
        ReadLockStrategy.None => NoReadLock.Instance,
        ReadLockStrategy.MarkerFile => MarkerFileReadLock.Instance,
        ReadLockStrategy.Changed => ChangedReadLock.Instance,
        ReadLockStrategy.FileLock => new FileLockReadLock(),
        ReadLockStrategy.Rename => new RenameReadLock(),
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown read lock strategy.")
    };
}
