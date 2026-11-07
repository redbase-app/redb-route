namespace redb.Route.Sftp;

/// <summary>
/// SOCKS/HTTP proxy types for tunneling SSH connections through corporate proxies.
/// Maps directly to SSH.NET's <c>ProxyTypes</c> enum.
/// </summary>
public enum SftpProxyType
{
    /// <summary>No proxy — direct connection.</summary>
    None,

    /// <summary>SOCKS4 proxy.</summary>
    Socks4,

    /// <summary>SOCKS5 proxy (supports username/password authentication).</summary>
    Socks5,

    /// <summary>HTTP CONNECT proxy.</summary>
    Http
}

/// <summary>
/// Strategy for moving the existing file at the producer target path before upload.
/// Used when <see cref="GenericFile.GenericFileExistStrategy.Move"/> is selected.
/// </summary>
public enum SftpMoveExistingStrategy
{
    /// <summary>Rename to {name}.bak (overwrite previous backup).</summary>
    Backup,

    /// <summary>Rename with timestamp suffix: {name}.yyyyMMddHHmmssfff</summary>
    Timestamp,

    /// <summary>Rename with GUID suffix: {name}.{guid}</summary>
    Guid
}

/// <summary>
/// Path separator mode for SFTP remote paths.
/// </summary>
public enum SftpSeparator
{
    /// <summary>Default: joins with "/" and additionally normalizes backslashes in the inputs.</summary>
    Auto,

    /// <summary>Strict Unix forward-slash: inputs are joined with "/" byte-for-byte.</summary>
    Unix

    // No Windows member: the SFTP protocol defines "/" as the path separator (SSH_FXP requests
    // carry canonical paths; servers on Windows accept "/" too). A backslash mode could never be
    // honest - the jail check, mkdir splitting, and recursive listing all speak "/" - so it was
    // removed the same way Binary/StepWise were (арка-ревью, H6).
}
