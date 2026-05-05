using redb.Route.GenericFile;

namespace redb.Route.Sftp;

/// <summary>
/// Endpoint options for SFTP transport. All properties are auto-bound from URI query parameters.
/// <para>
/// URI format: <c>sftp:///remote/path?host=server&amp;port=22&amp;username=admin&amp;password=secret&amp;...</c>
/// </para>
/// <para>
/// Inherits shared polling, post-processing, idempotency, and producer options from
/// <see cref="RemoteFileEndpointOptions"/>. Adds SFTP-specific: SSH authentication,
/// proxy tunneling, chmod, flatten, jail, and more.
/// </para>
/// </summary>
public class SftpEndpointOptions : RemoteFileEndpointOptions
{
    /// <summary>Creates SFTP endpoint options with protocol-specific defaults.</summary>
    public SftpEndpointOptions()
    {
        Delay = 60_000;
        InitialDelay = 1000;
        StartingDirectoryMustExist = true;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION: SFTP-specific
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>SFTP server port. (default: 22)</summary>
    public override int Port { get; set; } = 22;

    /// <summary>
    /// Path to a PEM/OpenSSH private key file for public-key authentication.
    /// Supports RSA, DSA, ECDSA, ED25519 keys.
    /// </summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>Passphrase for the encrypted private key (if applicable).</summary>
    public string PrivateKeyPassphrase { get; set; } = "";

    /// <summary>
    /// Enable keyboard-interactive authentication in addition to password/key.
    /// Useful for 2FA or custom PAM prompts. The password is sent as the response. (default: false)
    /// </summary>
    public bool UseKeyboardInteractive { get; set; }

    /// <summary>
    /// Preferred authentication order. Comma-separated: "publickey,password,keyboard-interactive".
    /// Empty = SSH.NET default (try all configured methods).
    /// </summary>
    public string PreferredAuthentications { get; set; } = "";

    /// <summary>
    /// Expected server host key fingerprint (MD5 hex, colon-separated, e.g. "AA:BB:CC:...").
    /// If set and StrictHostKeyChecking is true, the connection is rejected on mismatch.
    /// </summary>
    public string ServerFingerprint { get; set; } = "";

    /// <summary>
    /// If true, validate the server's host key against <see cref="ServerFingerprint"/>.
    /// If false (default), all host keys are accepted. (default: false)
    /// </summary>
    public bool StrictHostKeyChecking { get; set; }

    /// <summary>
    /// Path to a known_hosts file (OpenSSH format) for host key verification.
    /// Used only when <see cref="StrictHostKeyChecking"/> is true and ServerFingerprint is empty.
    /// </summary>
    public string KnownHostsFile { get; set; } = "";

    /// <summary>
    /// SSH keep-alive interval in milliseconds. 0 = disabled.
    /// Sends SSH_MSG_IGNORE packets to prevent firewall/NAT timeouts. (default: 0)
    /// </summary>
    public int KeepAliveInterval { get; set; }

    /// <summary>SSH.NET internal buffer size in bytes. (default: 32768 = 32 KB)</summary>
    public int BufferSize { get; set; } = 32_768;

    /// <summary>Enable zlib compression on the SSH channel. (default: false)</summary>
    public bool Compression { get; set; }

    // ── Proxy ────────────────────────────────────────────────────────

    /// <summary>Proxy type for tunneling the SSH connection. (default: None)</summary>
    public SftpProxyType ProxyType { get; set; } = SftpProxyType.None;

    /// <summary>Proxy hostname or IP.</summary>
    public string ProxyHost { get; set; } = "";

    /// <summary>Proxy port. (default: 1080)</summary>
    public int ProxyPort { get; set; } = 1080;

    /// <summary>Proxy username (for SOCKS5 or HTTP proxy auth).</summary>
    public string ProxyUsername { get; set; } = "";

    /// <summary>Proxy password.</summary>
    public string ProxyPassword { get; set; } = "";

    // ═══════════════════════════════════════════════════════════════════
    //  TRANSFER OPTIONS (SFTP-specific)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// If true, transfer files in binary mode (raw bytes). If false, use text mode with charset conversion.
    /// (default: true)
    /// </summary>
    public bool Binary { get; set; } = true;

    /// <summary>
    /// If true, use step-wise directory navigation (cd into each path component).
    /// If false, use the absolute path directly. Step-wise is safer with some servers. (default: true)
    /// </summary>
    public bool StepWise { get; set; } = true;

    /// <summary>Path separator mode for remote paths. (default: Auto)</summary>
    public SftpSeparator Separator { get; set; } = SftpSeparator.Auto;

    /// <summary>
    /// If true, silently ignore FileNotFoundException or permission errors during listing.
    /// Useful when the directory may not exist yet. (default: false)
    /// </summary>
    public bool IgnoreFileNotFoundOrPermissionError { get; set; }

    /// <summary>
    /// If true, each subdirectory must exist when using recursive mode.
    /// If false, silently skip missing subdirectories. (default: false)
    /// </summary>
    public bool DirectoryMustExist { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER: SFTP-specific
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Strategy for moving existing files when FileExist=Move. (default: Timestamp)
    /// </summary>
    public SftpMoveExistingStrategy MoveExistingFileStrategy { get; set; } = SftpMoveExistingStrategy.Timestamp;

    /// <summary>
    /// Set POSIX file permissions after upload (octal string, e.g. "0644").
    /// Empty = do not change permissions. Requires server support.
    /// </summary>
    public string Chmod { get; set; } = "";

    /// <summary>
    /// Set POSIX permissions on auto-created directories (octal string, e.g. "0755").
    /// Empty = server default.
    /// </summary>
    public string ChmodDirectory { get; set; } = "";

    /// <summary>
    /// If true, preserve the CamelFileLastModified header as the remote file's modification time.
    /// Requires SFTP v3+ set-stat support. (default: false)
    /// </summary>
    public bool KeepLastModified { get; set; }

    /// <summary>
    /// If true, flatten nested directory structures — all files are uploaded to the base directory.
    /// File names from subdirectories lose their directory prefix. (default: false)
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

        if (string.IsNullOrWhiteSpace(Username))
            throw new ArgumentException("SFTP Username must be specified.");

        if (string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(PrivateKeyPath))
            throw new ArgumentException("At least one authentication method required: provide Password or PrivateKeyPath.");

        if (BufferSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(BufferSize), BufferSize,
                "BufferSize must be at least 1024 bytes.");

        if (ProxyType != SftpProxyType.None && string.IsNullOrWhiteSpace(ProxyHost))
            throw new ArgumentException("ProxyHost must be specified when ProxyType is not None.");

        if (ProxyPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(ProxyPort), ProxyPort, "ProxyPort must be 1–65535.");

        if (!string.IsNullOrEmpty(Chmod) && !IsValidOctalPermissions(Chmod))
            throw new ArgumentException($"Chmod must be a valid octal permission string (e.g. '0644'), got: '{Chmod}'");

        if (!string.IsNullOrEmpty(ChmodDirectory) && !IsValidOctalPermissions(ChmodDirectory))
            throw new ArgumentException($"ChmodDirectory must be a valid octal permission string (e.g. '0755'), got: '{ChmodDirectory}'");
    }

    /// <summary>
    /// Parses an octal permissions string (e.g. "0644") to a short value for SFTP chmod.
    /// </summary>
    internal static short ParseOctalPermissions(string octal)
    {
        return Convert.ToInt16(octal, 8);
    }

    private static bool IsValidOctalPermissions(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            var v = Convert.ToInt32(value, 8);
            return v is >= 0 and <= 0x1FF; // 0000 to 0777
        }
        catch
        {
            return false;
        }
    }
}
