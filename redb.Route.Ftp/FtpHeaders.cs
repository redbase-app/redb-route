namespace redb.Route.Ftp;

/// <summary>
/// Constants for exchange headers set by the FTP component.
/// All prefixed with "redbFtp." to avoid collisions with other components.
/// </summary>
public static class FtpHeaders
{
    /// <summary>Common prefix for all FTP component headers.</summary>
    public const string Prefix = "redbFtp.";

    // ── Headers set on the In message by the consumer ────────────────

    /// <summary>Full file name with extension (e.g. "report.csv").</summary>
    public const string FileName = "redbFtp.Name";

    /// <summary>File name without the extension (e.g. "report").</summary>
    public const string FileNameOnly = "redbFtp.NameOnly";

    /// <summary>File extension including the dot (e.g. ".csv").</summary>
    public const string FileExtension = "redbFtp.Extension";

    /// <summary>Full remote path on the FTP server (e.g. "/upload/report.csv").</summary>
    public const string RemotePath = "redbFtp.RemotePath";

    /// <summary>Path relative to the polling base directory.</summary>
    public const string RelativePath = "redbFtp.RelativePath";

    /// <summary>Parent directory on the remote server.</summary>
    public const string RemoteParent = "redbFtp.RemoteParent";

    /// <summary>File size in bytes.</summary>
    public const string FileLength = "redbFtp.Length";

    /// <summary>Last modified date/time as DateTimeOffset (UTC).</summary>
    public const string FileLastModified = "redbFtp.LastModified";

    /// <summary>FTP server host that the file was retrieved from.</summary>
    public const string Host = "redbFtp.Host";

    /// <summary>FTP server port.</summary>
    public const string Port = "redbFtp.Port";

    /// <summary>Username used for the FTP connection.</summary>
    public const string Username = "redbFtp.Username";

    // ── Headers set by the producer after uploading ──────────────────

    /// <summary>Full remote path of the file that was actually written by the producer.</summary>
    public const string FileNameProduced = "redbFtp.NameProduced";
}
