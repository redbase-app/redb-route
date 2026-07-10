using System.Text;
using System.Web;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.File;

/// <summary>
/// Fluent API for File endpoints.
/// <example><code>
/// .From(FileDsl.Read("/data/input").Include("*.csv").Recursive().Noop())
/// .To(FileDsl.Write("/data/output").Charset("utf-8"))
/// </code></example>
/// </summary>
public static class FileDsl
{
    /// <summary>Poll a directory for files (consumer).</summary>
    public static FileBuilder Read(string directory) => new(directory);

    /// <summary>Write files to a directory (producer).</summary>
    public static FileBuilder Write(string directory) => new(directory);
}

/// <summary>
/// Fluent builder for File endpoint URIs.
/// </summary>
public sealed class FileBuilder
{
    private readonly string _directory;

    // Consumer (polling)
    private string? _delay;
    private string? _initialDelay;
    private string? _include;
    private string? _exclude;
    private bool _recursive;
    private string? _sortBy;
    private string? _maxMessagesPerPoll;
    private string? _minAge;

    // Post-processing
    private bool _noop;
    private bool _delete;
    private string? _moveTo;
    private string? _moveExisting;
    private string? _preMove;

    // Idempotency
    private bool _idempotent;
    private string? _idempotentKey;

    // Read locking
    private string? _readLock;
    private string? _readLockTimeout;
    private string? _readLockCheckInterval;
    private string? _readLockMinAge;
    private string? _readLockMarkerFileExtension;

    // Done file
    private string? _doneFileName;
    private bool _streamBody;

    // Producer (write)
    private string? _fileName;
    private string? _fileExist;
    private string? _tempPrefix;
    private string? _tempFileName;
    private string? _charset;
    private bool? _autoCreate;
    private bool _allowNullBody;
    private string? _appendChars;
    private bool? _eagerDeleteTargetFile;

    internal FileBuilder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    // ── Consumer ──────────────────────────────────────────────────────

    /// <summary>Poll delay in milliseconds. Default 500.</summary>
    public FileBuilder Delay(int ms) { _delay = ms.ToString(); return this; }
    /// <summary>Poll delay from an expression.</summary>
    public FileBuilder Delay(IExpression ms) { _delay = ms.ToTemplateString(); return this; }

    /// <summary>Initial delay before first poll. Default 0.</summary>
    public FileBuilder InitialDelay(int ms) { _initialDelay = ms.ToString(); return this; }
    /// <summary>Initial delay from an expression.</summary>
    public FileBuilder InitialDelay(IExpression ms) { _initialDelay = ms.ToTemplateString(); return this; }

    /// <summary>File name pattern to include (e.g. "*.csv").</summary>
    public FileBuilder Include(string pattern) { _include = pattern; return this; }

    /// <summary>File name pattern to exclude.</summary>
    public FileBuilder Exclude(string pattern) { _exclude = pattern; return this; }

    /// <summary>Recurse into subdirectories.</summary>
    public FileBuilder Recursive() { _recursive = true; return this; }

    /// <summary>Sort order: None, Name, NameDesc, Modified, ModifiedDesc, Size, SizeDesc.</summary>
    public FileBuilder SortBy(string sortBy) { _sortBy = sortBy; return this; }

    /// <summary>Max files per poll. Default 0 (unlimited).</summary>
    public FileBuilder MaxMessagesPerPoll(int max) { _maxMessagesPerPoll = max.ToString(); return this; }
    /// <summary>Max files per poll from an expression.</summary>
    public FileBuilder MaxMessagesPerPoll(IExpression max) { _maxMessagesPerPoll = max.ToTemplateString(); return this; }

    /// <summary>Minimum file age in milliseconds before processing.</summary>
    public FileBuilder MinAge(long ms) { _minAge = ms.ToString(); return this; }
    /// <summary>Minimum file age from an expression.</summary>
    public FileBuilder MinAge(IExpression ms) { _minAge = ms.ToTemplateString(); return this; }

    // ── Post-processing ───────────────────────────────────────────────

    /// <summary>Do nothing after processing (leave file in place).</summary>
    public FileBuilder Noop() { _noop = true; return this; }

    /// <summary>Delete file after processing.</summary>
    public FileBuilder Delete() { _delete = true; return this; }

    /// <summary>Move processed file to this directory.</summary>
    public FileBuilder MoveTo(IExpression directory) { _moveTo = directory.ToTemplateString(); return this; }
    /// <summary>Move processed file to this directory (template string, supports ${...}).</summary>
    public FileBuilder MoveTo(string directory) => MoveTo(new StringExpression(directory));

    /// <summary>Strategy when file exists in move target: Override, Append, Fail, Ignore, Move, TryRename.</summary>
    public FileBuilder MoveExisting(string strategy) { _moveExisting = strategy; return this; }

    /// <summary>Pre-move directory (temporary move before processing).</summary>
    public FileBuilder PreMove(IExpression directory) { _preMove = directory.ToTemplateString(); return this; }
    /// <summary>Pre-move directory (template string, supports ${...}).</summary>
    public FileBuilder PreMove(string directory) => PreMove(new StringExpression(directory));

    // ── Idempotency ───────────────────────────────────────────────────

    /// <summary>Enable idempotent consumer with an optional key expression.</summary>
    public FileBuilder Idempotent(IExpression? key = null) { _idempotent = true; _idempotentKey = key?.ToTemplateString(); return this; }
    /// <summary>Enable idempotent consumer with a key (template string, supports ${...}).</summary>
    public FileBuilder Idempotent(string key) => Idempotent(new StringExpression(key));

    // ── Read locking ──────────────────────────────────────────────────

    /// <summary>Read lock strategy: None, MarkerFile, Changed, FileLock, Rename.</summary>
    public FileBuilder ReadLock(string strategy) { _readLock = strategy; return this; }

    /// <summary>Read lock timeout in milliseconds. Default 10000.</summary>
    public FileBuilder ReadLockTimeout(long ms) { _readLockTimeout = ms.ToString(); return this; }
    /// <summary>Read lock timeout from an expression.</summary>
    public FileBuilder ReadLockTimeout(IExpression ms) { _readLockTimeout = ms.ToTemplateString(); return this; }

    /// <summary>Read lock check interval in milliseconds. Default 1000.</summary>
    public FileBuilder ReadLockCheckInterval(int ms) { _readLockCheckInterval = ms.ToString(); return this; }
    /// <summary>Read lock check interval from an expression.</summary>
    public FileBuilder ReadLockCheckInterval(IExpression ms) { _readLockCheckInterval = ms.ToTemplateString(); return this; }

    /// <summary>Minimum file age for Changed read lock. Default 1000.</summary>
    public FileBuilder ReadLockMinAge(long ms) { _readLockMinAge = ms.ToString(); return this; }
    /// <summary>Read lock min age from an expression.</summary>
    public FileBuilder ReadLockMinAge(IExpression ms) { _readLockMinAge = ms.ToTemplateString(); return this; }

    /// <summary>Marker file extension for MarkerFile read lock. Default ".redbLock".</summary>
    public FileBuilder ReadLockMarkerFileExtension(string ext) { _readLockMarkerFileExtension = ext; return this; }

    // ── Done file ─────────────────────────────────────────────────────

    /// <summary>Done file name pattern. If set, only files with a matching done file are processed.</summary>
    public FileBuilder DoneFileName(IExpression name) { _doneFileName = name.ToTemplateString(); return this; }
    /// <summary>Done file name pattern (template string, supports ${...}).</summary>
    public FileBuilder DoneFileName(string name) => DoneFileName(new StringExpression(name));

    /// <summary>Set exchange body to a FileStream instead of byte[]. Stream is closed by Exchange.DisposeAsync.</summary>
    public FileBuilder StreamBody() { _streamBody = true; return this; }

    // ── Producer ──────────────────────────────────────────────────────

    /// <summary>Output file name or expression (e.g. <c>Header("CamelFileName")</c>).</summary>
    public FileBuilder FileName(IExpression name) { _fileName = name.ToTemplateString(); return this; }
    /// <summary>Output file name (template string, supports ${...}).</summary>
    public FileBuilder FileName(string name) => FileName(new StringExpression(name));

    /// <summary>Strategy when target file exists: Override, Append, Fail, Ignore, Move, TryRename.</summary>
    public FileBuilder FileExist(string strategy) { _fileExist = strategy; return this; }

    /// <summary>Temporary file prefix during write.</summary>
    public FileBuilder TempPrefix(IExpression prefix) { _tempPrefix = prefix.ToTemplateString(); return this; }
    /// <summary>Temporary file prefix during write (template string, supports ${...}).</summary>
    public FileBuilder TempPrefix(string prefix) => TempPrefix(new StringExpression(prefix));

    /// <summary>Temporary file name during write.</summary>
    public FileBuilder TempFileName(IExpression name) { _tempFileName = name.ToTemplateString(); return this; }
    /// <summary>Temporary file name during write (template string, supports ${...}).</summary>
    public FileBuilder TempFileName(string name) => TempFileName(new StringExpression(name));

    /// <summary>Character encoding. Default "utf-8".</summary>
    public FileBuilder Charset(string charset) { _charset = charset; return this; }

    /// <summary>Auto-create target directory. Default true.</summary>
    public FileBuilder AutoCreate(bool autoCreate = true) { _autoCreate = autoCreate; return this; }

    /// <summary>Allow sending null body (creates empty file).</summary>
    public FileBuilder AllowNullBody() { _allowNullBody = true; return this; }

    /// <summary>Characters to append after writing (e.g. newline).</summary>
    public FileBuilder AppendChars(string chars) { _appendChars = chars; return this; }

    /// <summary>Eagerly delete target file before writing. Default true.</summary>
    public FileBuilder EagerDeleteTargetFile(bool eager = true) { _eagerDeleteTargetFile = eager; return this; }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the File endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("file:///");
        sb.Append(_directory.TrimStart('/'));

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (!string.IsNullOrEmpty(value)) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value)
        {
            if (value.HasValue) Append(key, value.Value.ToString().ToLowerInvariant());
        }

        // Consumer
        AppendIf("delay", _delay);
        AppendIf("initialDelay", _initialDelay);
        AppendIf("include", _include);
        AppendIf("exclude", _exclude);
        AppendBool("recursive", _recursive);
        AppendIf("sortBy", _sortBy);
        AppendIf("maxMessagesPerPoll", _maxMessagesPerPoll);
        AppendIf("minAge", _minAge);

        // Post-processing
        AppendBool("noop", _noop);
        AppendBool("delete", _delete);
        AppendIf("moveTo", _moveTo);
        AppendIf("moveExisting", _moveExisting);
        AppendIf("preMove", _preMove);

        // Idempotency
        AppendBool("idempotent", _idempotent);
        AppendIf("idempotentKey", _idempotentKey);

        // Read locking
        AppendIf("readLock", _readLock);
        AppendIf("readLockTimeout", _readLockTimeout);
        AppendIf("readLockCheckInterval", _readLockCheckInterval);
        AppendIf("readLockMinAge", _readLockMinAge);
        AppendIf("readLockMarkerFileExtension", _readLockMarkerFileExtension);

        // Done file
        AppendIf("doneFileName", _doneFileName);

        // Body
        AppendBool("streamBody", _streamBody);

        // Producer
        AppendIf("fileName", _fileName);
        AppendIf("fileExist", _fileExist);
        AppendIf("tempPrefix", _tempPrefix);
        AppendIf("tempFileName", _tempFileName);
        AppendIf("charset", _charset);
        AppendBoolExplicit("autoCreate", _autoCreate);
        AppendBool("allowNullBody", _allowNullBody);
        AppendIf("appendChars", _appendChars);
        AppendBoolExplicit("eagerDeleteTargetFile", _eagerDeleteTargetFile);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(FileBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
