namespace redb.Route.File;

/// <summary>
/// Constants for exchange headers and properties set by the file component.
/// All prefixed with "redbFile." to avoid collisions.
/// </summary>
public static class FileHeaders
{
    /// <summary>Common prefix for all File component headers.</summary>
    public const string Prefix = "redbFile.";

    // ── Headers set on the In message by the consumer ────────────────

    /// <summary>Full file name with extension (e.g. "order.csv").</summary>
    public const string FileName = "redbFile.Name";

    /// <summary>File name without the extension (e.g. "order").</summary>
    public const string FileNameOnly = "redbFile.NameOnly";

    /// <summary>File extension including the dot (e.g. ".csv").</summary>
    public const string FileExtension = "redbFile.Extension";

    /// <summary>Full absolute path of the file.</summary>
    public const string FileAbsolutePath = "redbFile.AbsolutePath";

    /// <summary>Path relative to the polling directory.</summary>
    public const string FileRelativePath = "redbFile.RelativePath";

    /// <summary>Parent directory of the file.</summary>
    public const string FileParent = "redbFile.Parent";

    /// <summary>File size in bytes.</summary>
    public const string FileLength = "redbFile.Length";

    /// <summary>Last modified date/time as DateTimeOffset.</summary>
    public const string FileLastModified = "redbFile.LastModified";

    // ── Headers set by the producer after writing ────────────────────

    /// <summary>Absolute path of the file that was actually written by the producer.</summary>
    public const string FileNameProduced = "redbFile.NameProduced";
}
