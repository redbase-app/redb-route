using redb.Route.Core;

namespace redb.Route.GenericFile;

/// <summary>
/// Base endpoint options shared by all file-based transports (local file, SFTP, FTP).
/// Provides consumer polling, post-processing, idempotency, done-file, and producer write options.
/// </summary>
public abstract class GenericFileEndpointOptions : EndpointOptions
{
    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER: Polling
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Polling delay in milliseconds between directory scans. (default: 500)</summary>
    public int Delay { get; set; } = 500;

    /// <summary>Initial delay before the first poll in milliseconds. (default: 0)</summary>
    public int InitialDelay { get; set; }

    /// <summary>Glob pattern to include files (e.g. "*.csv"). Multiple patterns: "*.csv,*.json". Empty = all files.</summary>
    public string Include { get; set; } = "";

    /// <summary>Glob pattern to exclude files (e.g. "*.tmp,.*"). Empty = exclude nothing.</summary>
    public string Exclude { get; set; } = "";

    /// <summary>Whether to recurse into subdirectories. (default: false)</summary>
    public bool Recursive { get; set; }

    /// <summary>Maximum recursion depth when Recursive=true. 0 = unlimited. (default: 0)</summary>
    public int MaxDepth { get; set; }

    /// <summary>Minimum recursion depth. Files at shallower levels are skipped. (default: 0)</summary>
    public int MinDepth { get; set; }

    /// <summary>Sort order for polled files. (default: None)</summary>
    public GenericFileSortBy SortBy { get; set; } = GenericFileSortBy.None;

    /// <summary>Maximum number of files to pick up per poll. 0 = unlimited. (default: 0)</summary>
    public int MaxMessagesPerPoll { get; set; }

    /// <summary>Minimum file age in milliseconds before the file is eligible for pickup. (default: 0)</summary>
    public long MinAge { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER: Post-processing
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>If true, the file is not moved or deleted after processing (read-only mode). (default: false)</summary>
    public bool Noop { get; set; }

    /// <summary>If true, delete the file after successful processing. (default: false)</summary>
    public bool Delete { get; set; }

    /// <summary>Directory to move files to after successful processing. Supports ${file:name} expressions.</summary>
    public string MoveTo { get; set; } = "";

    /// <summary>Strategy when the target file in MoveTo already exists. (default: Override)</summary>
    public GenericFileExistStrategy MoveExisting { get; set; } = GenericFileExistStrategy.Override;

    /// <summary>Directory to move files to BEFORE processing (pre-move for concurrency safety).</summary>
    public string PreMove { get; set; } = "";

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER: Idempotency
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>If true, skip files that have already been processed (tracked by idempotent key). (default: false)</summary>
    public bool Idempotent { get; set; }

    /// <summary>Expression for the idempotent key. Default uses path + last modified + size.</summary>
    public string IdempotentKey { get; set; } = "";

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER: Done file
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pattern for a "done" marker file. If set, the consumer waits for this marker before processing.
    /// Example: "${file:name}.done" — processes "order.csv" only when "order.csv.done" exists.
    /// </summary>
    public string DoneFileName { get; set; } = "";

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER: Body
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// If true, the consumer sets the exchange body to a Stream instead of reading
    /// the entire file into a byte array. The stream is closed by Exchange.DisposeAsync(). (default: false)
    /// </summary>
    public bool StreamBody { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER: Write
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Dynamic file name for the producer. Supports ${header.xxx} expressions.</summary>
    public DynamicValue<string>? FileName { get; set; }

    /// <summary>Strategy when the target file already exists. (default: Override)</summary>
    public GenericFileExistStrategy FileExist { get; set; } = GenericFileExistStrategy.Override;

    /// <summary>Prefix for the temp file during write (e.g. ".redb_"). Empty = write directly.</summary>
    public string TempPrefix { get; set; } = "";

    /// <summary>Full temp file name pattern. Overrides TempPrefix if set.</summary>
    public string TempFileName { get; set; } = "";

    /// <summary>File encoding (default: "utf-8").</summary>
    public string Charset { get; set; } = "utf-8";

    /// <summary>If true, create the target directory if it doesn't exist. (default: true)</summary>
    public bool AutoCreate { get; set; } = true;

    /// <summary>If true, allow writing an empty file when body is null. (default: false)</summary>
    public bool AllowNullBody { get; set; }

    /// <summary>Characters to append after each Append write (e.g. "\n"). Empty = nothing.</summary>
    public string AppendChars { get; set; } = "";

    /// <summary>If true, delete the target file before writing when using Override strategy. (default: true)</summary>
    public bool EagerDeleteTargetFile { get; set; } = true;

    // ═══════════════════════════════════════════════════════════════════
    //  VALIDATION (base — subclasses should call base.ValidateCommon())
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates common file endpoint options. Subclasses should call this from their Validate() method.
    /// </summary>
    protected void ValidateCommon()
    {
        if (Delay < 0)
            throw new ArgumentOutOfRangeException(nameof(Delay), Delay, "Delay cannot be negative.");

        if (InitialDelay < 0)
            throw new ArgumentOutOfRangeException(nameof(InitialDelay), InitialDelay, "InitialDelay cannot be negative.");

        if (MaxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDepth), MaxDepth, "MaxDepth cannot be negative.");

        if (MinDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(MinDepth), MinDepth, "MinDepth cannot be negative.");

        if (MaxMessagesPerPoll < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxMessagesPerPoll), MaxMessagesPerPoll,
                "MaxMessagesPerPoll cannot be negative.");

        if (MinAge < 0)
            throw new ArgumentOutOfRangeException(nameof(MinAge), MinAge, "MinAge cannot be negative.");

        if (Noop && Delete)
            throw new InvalidOperationException("Cannot set both Noop=true and Delete=true.");

        if (Noop && !string.IsNullOrEmpty(MoveTo))
            throw new InvalidOperationException("Cannot set both Noop=true and MoveTo.");

        if (Delete && !string.IsNullOrEmpty(MoveTo))
            throw new InvalidOperationException("Cannot set both Delete=true and MoveTo. Choose one post-processing strategy.");
    }
}
