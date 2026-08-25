using redb.Route.GenericFile;

namespace redb.Route.File;

/// <summary>
/// Options for file endpoints. Inherits shared options from <see cref="GenericFileEndpointOptions"/>
/// and adds local-file-specific read lock configuration.
/// URI: file:///C:/path?include=*.csv&amp;delay=5000&amp;noop=true
/// </summary>
public class FileEndpointOptions : GenericFileEndpointOptions
{
    // ── Read locking (local file only) ──────────────────────────────

    /// <summary>Read lock strategy (default: None).</summary>
    public ReadLockStrategy ReadLock { get; set; } = ReadLockStrategy.None;

    /// <summary>Timeout in milliseconds for acquiring a read lock (default: 10000).</summary>
    public long ReadLockTimeout { get; set; } = 10000;

    /// <summary>Interval in milliseconds between read lock checks for 'Changed' strategy (default: 1000).</summary>
    public int ReadLockCheckInterval { get; set; } = 1000;

    /// <summary>Minimum age in milliseconds for 'Changed' strategy — file must be unchanged for this long (default: 1000).</summary>
    public long ReadLockMinAge { get; set; } = 1000;

    /// <summary>Extension used for marker file lock (default: ".redbLock").</summary>
    public string ReadLockMarkerFileExtension { get; set; } = ".redbLock";

    // ── Producer safety ─────────────────────────────────────────────

    /// <summary>
    /// If true, the producer refuses to write outside the endpoint directory (default: true).
    /// File names usually arrive from the incoming message, so a "../" or absolute name would
    /// otherwise escape the directory. Set to false only when writing outside is intended.
    /// </summary>
    public bool JailStartingDirectory { get; set; } = true;

    /// <inheritdoc />
    public override void Validate()
    {
        ValidateCommon();

        if (ReadLockCheckInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(ReadLockCheckInterval), ReadLockCheckInterval,
                "ReadLockCheckInterval must be positive.");

        if (ReadLockMinAge < 0)
            throw new ArgumentOutOfRangeException(nameof(ReadLockMinAge), ReadLockMinAge,
                "ReadLockMinAge cannot be negative.");

        if (ReadLockTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(ReadLockTimeout), ReadLockTimeout,
                "ReadLockTimeout cannot be negative.");
    }
}
