namespace redb.Route.GenericFile;

/// <summary>
/// Protocol-agnostic file metadata. Used by consumers and producers
/// to represent files from any source (local FS, SFTP, FTP).
/// </summary>
public sealed class GenericFileInfo
{
    /// <summary>File name with extension (e.g. "report.csv").</summary>
    public required string Name { get; init; }

    /// <summary>Full path: absolute path for local, remote path for SFTP/FTP.</summary>
    public required string FullPath { get; init; }

    /// <summary>Base polling directory from which this file was enumerated.</summary>
    public required string BasePath { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>Last modified timestamp (UTC).</summary>
    public required DateTimeOffset LastModified { get; init; }

    /// <summary>Recursion depth: 0 = root dir, 1 = first subdirectory, etc.</summary>
    public int Depth { get; init; }

    /// <summary>
    /// Protocol-specific extras (e.g. SFTP UserId/GroupId, FTP permissions).
    /// Null if no extras are available.
    /// </summary>
    public Dictionary<string, object>? Extras { get; init; }
}
