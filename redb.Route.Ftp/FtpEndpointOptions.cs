using redb.Route.GenericFile;

namespace redb.Route.Ftp;

/// <summary>
/// Endpoint options for FTP transport. All properties are auto-bound from URI query parameters.
/// <para>
/// URI format: <c>ftp:///remote/path?host=server&amp;port=21&amp;username=admin&amp;password=secret&amp;...</c>
/// </para>
/// <para>
/// Inherits shared polling, post-processing, idempotency, and producer options from
/// <see cref="RemoteFileEndpointOptions"/>. Adds FTP-specific: passive mode, FTPS/TLS,
/// transfer type, and more.
/// </para>
/// </summary>
public class FtpEndpointOptions : RemoteFileEndpointOptions
{
    /// <summary>Creates FTP endpoint options with protocol-specific defaults.</summary>
    public FtpEndpointOptions()
    {
        Delay = 60_000;
        InitialDelay = 1000;
        StartingDirectoryMustExist = true;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION: FTP-specific
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>FTP server port. (default: 21)</summary>
    public override int Port { get; set; } = 21;

    /// <summary>
    /// Use passive mode for data connections. Most firewalls/NATs require passive mode.
    /// (default: true)
    /// </summary>
    public bool PassiveMode { get; set; } = true;

    // ═══════════════════════════════════════════════════════════════════
    //  TLS / FTPS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enable FTP over TLS (FTPS). When true, uses explicit TLS negotiation.
    /// (default: false)
    /// </summary>
    public bool UseFtps { get; set; }

    /// <summary>
    /// If true, accept any server certificate (useful for self-signed certs in dev/test).
    /// If false, validate server certificate against system trust store. (default: true)
    /// </summary>
    public bool ValidateCertificate { get; set; } = true;

    // ═══════════════════════════════════════════════════════════════════
    //  TRANSFER OPTIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Transfer type: Binary (raw bytes, default) or Ascii (line-ending conversion).
    /// </summary>
    public FtpTransferType TransferType { get; set; } = FtpTransferType.Binary;

    /// <summary>
    /// If true, silently ignore file-not-found or permission errors during listing.
    /// (default: false)
    /// </summary>
    public bool IgnoreFileNotFoundOrPermissionError { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER: FTP-specific
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Strategy for moving existing files when FileExist=Move. (default: Timestamp)
    /// </summary>
    public FtpMoveExistingStrategy MoveExistingFileStrategy { get; set; } = FtpMoveExistingStrategy.Timestamp;

    /// <summary>
    /// If true, flatten nested directory structures — all files are uploaded to the base directory.
    /// (default: false)
    /// </summary>
    public bool Flatten { get; set; }

    /// <summary>
    /// If true, prevent the producer from writing outside the base directory.
    /// FileName expressions that navigate with ".." are rejected. (default: true)
    /// </summary>
    public bool JailStartingDirectory { get; set; } = true;

    // ═══════════════════════════════════════════════════════════════════
    //  VALIDATION
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public override void Validate()
    {
        ValidateRemote();

        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("FTP Host must be specified.");
    }
}
