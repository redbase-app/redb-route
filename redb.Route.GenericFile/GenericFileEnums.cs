namespace redb.Route.GenericFile;

/// <summary>
/// Strategy for how the producer handles an existing file at the target path.
/// Shared across all file-based transports (local, SFTP, FTP).
/// </summary>
public enum GenericFileExistStrategy
{
    /// <summary>Overwrite the existing file.</summary>
    Override,

    /// <summary>Append to the existing file.</summary>
    Append,

    /// <summary>Throw an exception if the file exists.</summary>
    Fail,

    /// <summary>Do nothing — silently skip writing if the file exists.</summary>
    Ignore,

    /// <summary>Move the existing file to a backup before writing the new one.</summary>
    Move,

    /// <summary>Try to rename the existing file before writing. Fail silently if rename fails.</summary>
    TryRename
}

/// <summary>
/// Sort criteria for ordering files during consumer polling.
/// Shared across all file-based transports (local, SFTP, FTP).
/// </summary>
public enum GenericFileSortBy
{
    /// <summary>No sorting — OS/server-native order.</summary>
    None,

    /// <summary>Sort by file name ascending (A → Z).</summary>
    Name,

    /// <summary>Sort by file name descending (Z → A).</summary>
    NameDesc,

    /// <summary>Sort by last modified time ascending (oldest first).</summary>
    Modified,

    /// <summary>Sort by last modified time descending (newest first).</summary>
    ModifiedDesc,

    /// <summary>Sort by file size ascending (smallest first).</summary>
    Size,

    /// <summary>Sort by file size descending (largest first).</summary>
    SizeDesc
}
