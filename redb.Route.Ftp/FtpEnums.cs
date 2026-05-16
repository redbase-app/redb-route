namespace redb.Route.Ftp;

/// <summary>
/// FTP data transfer type.
/// </summary>
public enum FtpTransferType
{
    /// <summary>Binary transfer — no conversion (default).</summary>
    Binary,

    /// <summary>ASCII transfer — line-ending conversion.</summary>
    Ascii
}

/// <summary>
/// Strategy for moving the existing file at the producer target path before upload.
/// Used when <see cref="GenericFile.GenericFileExistStrategy.Move"/> is selected.
/// </summary>
public enum FtpMoveExistingStrategy
{
    /// <summary>Rename to {name}.bak (overwrite previous backup).</summary>
    Backup,

    /// <summary>Rename with timestamp suffix: {name}.yyyyMMddHHmmssfff</summary>
    Timestamp,

    /// <summary>Rename with GUID suffix: {name}.{guid}</summary>
    Guid
}
