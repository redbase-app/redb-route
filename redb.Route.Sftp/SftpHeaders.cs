namespace redb.Route.Sftp;

/// <summary>
/// Constants for exchange headers set by the SFTP component.
/// All prefixed with "redbSftp." to avoid collisions with other components.
/// </summary>
public static class SftpHeaders
{
    /// <summary>Common prefix for all SFTP component headers.</summary>
    public const string Prefix = "redbSftp.";

    // ── Headers set on the In message by the consumer ────────────────

    /// <summary>Full file name with extension (e.g. "report.csv").</summary>
    public const string FileName = "redbSftp.Name";

    /// <summary>File name without the extension (e.g. "report").</summary>
    public const string FileNameOnly = "redbSftp.NameOnly";

    /// <summary>File extension including the dot (e.g. ".csv").</summary>
    public const string FileExtension = "redbSftp.Extension";

    /// <summary>Full remote path on the SFTP server (e.g. "/upload/report.csv").</summary>
    public const string RemotePath = "redbSftp.RemotePath";

    /// <summary>Path relative to the polling base directory.</summary>
    public const string RelativePath = "redbSftp.RelativePath";

    /// <summary>Parent directory on the remote server.</summary>
    public const string RemoteParent = "redbSftp.RemoteParent";

    /// <summary>File size in bytes.</summary>
    public const string FileLength = "redbSftp.Length";

    /// <summary>Last modified date/time as DateTimeOffset (UTC).</summary>
    public const string FileLastModified = "redbSftp.LastModified";

    /// <summary>File permissions (octal string, e.g. "0644").</summary>
    public const string FilePermissions = "redbSftp.Permissions";

    /// <summary>File owner UID.</summary>
    public const string FileOwner = "redbSftp.Owner";

    /// <summary>File group GID.</summary>
    public const string FileGroup = "redbSftp.Group";

    /// <summary>SFTP server host that the file was retrieved from.</summary>
    public const string Host = "redbSftp.Host";

    /// <summary>SFTP server port.</summary>
    public const string Port = "redbSftp.Port";

    /// <summary>Username used for the SFTP connection.</summary>
    public const string Username = "redbSftp.Username";

    // ── Headers set by the producer after uploading ──────────────────

    /// <summary>Full remote path of the file that was actually written by the producer.</summary>
    public const string FileNameProduced = "redbSftp.NameProduced";
}
