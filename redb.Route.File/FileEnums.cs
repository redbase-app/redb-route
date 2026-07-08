namespace redb.Route.File;

/// <summary>
/// Strategy for handling read locks on files being consumed.
/// Determines how the consumer ensures a file is not currently being written to.
/// </summary>
public enum ReadLockStrategy
{
    /// <summary>No read lock. File is picked up immediately.</summary>
    None,

    /// <summary>
    /// Creates a marker file (*.redbLock) next to the target file.
    /// Other consumers skip files with an existing lock marker.
    /// </summary>
    MarkerFile,

    /// <summary>
    /// Waits until the file size stops changing for a configurable interval.
    /// Good for large files being uploaded/written.
    /// </summary>
    Changed,

    /// <summary>
    /// Uses OS-level exclusive file lock (FileShare.None).
    /// If the lock fails, the file is skipped until next poll.
    /// </summary>
    FileLock,

    /// <summary>
    /// Attempts to rename the file. If rename fails, the file is locked by another process.
    /// </summary>
    Rename
}
