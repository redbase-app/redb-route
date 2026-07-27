using redb.Route.Core;

namespace redb.Route.GenericFile;

/// <summary>
/// Base endpoint options for remote file-based transports (SFTP, FTP).
/// Extends <see cref="GenericFileEndpointOptions"/> with connection parameters,
/// reconnection logic, and remote-specific consumer features.
/// </summary>
public abstract class RemoteFileEndpointOptions : GenericFileEndpointOptions
{
    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Remote server hostname or IP address. (default: localhost)</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Remote server port. Subclass should set the protocol-specific default.</summary>
    public virtual int Port { get; set; }

    /// <summary>
    /// Named <see cref="RemoteFileConnectionFactory"/> from the route registry. Lets server
    /// credentials live in the registry instead of the endpoint URI, so they never reach logs
    /// or dashboards.
    /// </summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Username for authentication.</summary>
    public string Username { get; set; } = "";

    /// <summary>Password for authentication.</summary>
    [Sensitive]
    public string Password { get; set; } = "";

    /// <summary>TCP connection timeout in milliseconds. (default: 30000 = 30 s)</summary>
    public int ConnectionTimeout { get; set; } = 30_000;

    /// <summary>Operation timeout in milliseconds (read/write/list). (default: 60000 = 60 s)</summary>
    public int OperationTimeout { get; set; } = 60_000;

    // ═══════════════════════════════════════════════════════════════════
    //  RECONNECTION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Maximum number of automatic reconnect attempts after a disconnect. (default: 3)</summary>
    public int MaximumReconnectAttempts { get; set; } = 3;

    /// <summary>Delay in milliseconds between reconnect attempts. (default: 1000)</summary>
    public int ReconnectDelay { get; set; } = 1000;

    /// <summary>
    /// If true, disconnect after each poll cycle (consumer) or each upload (producer).
    /// Reduces open connections at the cost of latency. (default: false)
    /// </summary>
    public bool Disconnect { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER: Remote-specific
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Maximum file age in milliseconds. Files older than this are skipped. 0 = no limit. (default: 0)</summary>
    public long MaxAge { get; set; }

    /// <summary>Directory to move files to on processing failure. Empty = leave in place.</summary>
    public string MoveFailed { get; set; } = "";

    /// <summary>
    /// If true, the starting (base) directory must exist on the remote server.
    /// Throws if it doesn't. (default: false)
    /// </summary>
    public bool StartingDirectoryMustExist { get; set; }

    /// <summary>
    /// If true, the consumer creates an empty exchange when a poll returns no files.
    /// Useful for heartbeat-style monitoring. (default: false)
    /// </summary>
    public bool SendEmptyMessageWhenIdle { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  VALIDATION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates remote-specific options. Subclasses should call this from their Validate() method.
    /// </summary>
    protected void ValidateRemote()
    {
        ValidateCommon();

        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("Host must be specified.");

        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be 1–65535.");

        if (MaxAge < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxAge), MaxAge, "MaxAge cannot be negative.");

        if (ConnectionTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout), ConnectionTimeout,
                "ConnectionTimeout cannot be negative.");

        if (OperationTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout), OperationTimeout,
                "OperationTimeout cannot be negative.");

        if (MaximumReconnectAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumReconnectAttempts), MaximumReconnectAttempts,
                "MaximumReconnectAttempts cannot be negative.");

        if (ReconnectDelay < 0)
            throw new ArgumentOutOfRangeException(nameof(ReconnectDelay), ReconnectDelay,
                "ReconnectDelay cannot be negative.");
    }
}
