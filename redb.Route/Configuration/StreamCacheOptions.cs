namespace redb.Route.Configuration;

/// <summary>
/// Options for stream caching behavior.
/// </summary>
public sealed class StreamCacheOptions
{
    /// <summary>Threshold in bytes before spooling from memory to a temp file. Default: 128 KB.</summary>
    public long SpoolThreshold { get; set; } = 128 * 1024;

    /// <summary>Directory for temporary spool files. Default: system temp directory.</summary>
    public string? TempDirectory { get; set; }

    /// <summary>Enable stream caching globally for all routes. Default: false.</summary>
    public bool Enabled { get; set; }
}
