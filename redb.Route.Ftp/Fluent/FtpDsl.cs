using System.Text;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Ftp;

/// <summary>
/// Fluent API for FTP endpoints.
/// <example><code>
/// .From(Ftp.Directory("/incoming").Host("ftp.example.com").Username("admin").Password("secret")
///     .Include("*.csv").Recursive().Delete())
/// .To(Ftp.Directory("/outgoing").Host("ftp.example.com").Username("admin").Password("secret")
///     .AutoCreate())
/// </code></example>
/// </summary>
public static class Ftp
{
    /// <summary>Creates an FTP endpoint for the given remote directory path.</summary>
    public static FtpBuilder Directory(string remotePath) => new(remotePath);
}

/// <summary>
/// Fluent builder for FTP endpoint URIs.
/// </summary>
public sealed class FtpBuilder
{
    private readonly string _remotePath;

    // Connection
    private string? _host;
    private string? _port;
    private string? _username;
    private string? _password;
    private string? _connectionFactory;
    private string? _connectionTimeout;
    private string? _operationTimeout;
    private bool _passiveMode = true;
    private bool _passiveModeExplicit;

    // TLS
    private bool _useFtps;
    private bool? _validateCertificate;

    // Transfer
    private string? _transferType;
    private bool _streamBody;
    private string? _charset;
    private bool _ignoreFileNotFoundOrPermissionError;
    private bool? _startingDirectoryMustExist;
    private bool _sendEmptyMessageWhenIdle;

    // Reconnection
    private string? _maximumReconnectAttempts;
    private string? _reconnectDelay;
    private bool _disconnect;

    // Consumer
    private string? _delay;
    private string? _initialDelay;
    private string? _include;
    private string? _exclude;
    private bool _recursive;
    private string? _maxDepth;
    private string? _minDepth;
    private string? _sortBy;
    private string? _maxMessagesPerPoll;
    private string? _minAge;
    private string? _maxAge;

    // Post-processing
    private bool _noop;
    private bool _delete;
    private string? _moveTo;
    private string? _moveExisting;
    private string? _preMove;
    private string? _moveFailed;

    // Idempotency
    private bool _idempotent;
    private string? _idempotentKey;
    private string? _doneFileName;

    // Producer
    private string? _fileName;
    private string? _fileExist;
    private string? _moveExistingFileStrategy;
    private string? _tempPrefix;
    private string? _tempFileName;
    private bool? _autoCreate;
    private bool _allowNullBody;
    private bool? _eagerDeleteTargetFile;
    private bool _flatten;
    private bool? _jailStartingDirectory;
    private string? _appendChars;

    internal FtpBuilder(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        _remotePath = remotePath;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>FTP server hostname.</summary>
    public FtpBuilder Host(IExpression host) { _host = host.ToTemplateString(); return this; }

    /// <summary>FTP server hostname (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder Host(string host) => Host(new StringExpression(host));

    /// <summary>FTP server port. Default 21.</summary>
    public FtpBuilder Port(int port) { _port = port.ToString(); return this; }

    /// <summary>FTP server port from an expression.</summary>
    public FtpBuilder Port(IExpression port) { _port = port.ToTemplateString(); return this; }

    /// <summary>Username for authentication.</summary>
    public FtpBuilder Username(IExpression username) { _username = username.ToTemplateString(); return this; }

    /// <summary>Username for authentication (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder Username(string username) => Username(new StringExpression(username));

    /// <summary>Password for authentication.</summary>
    public FtpBuilder Password(IExpression password) { _password = password.ToTemplateString(); return this; }

    /// <summary>Password for authentication (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder Password(string password) => Password(new StringExpression(password));

    /// <summary>
    /// References a named <see cref="FtpConnectionFactory"/> from the route registry instead of
    /// putting server credentials in the URI.
    /// </summary>
    public FtpBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>Connection timeout in milliseconds. Default 30000.</summary>
    public FtpBuilder ConnectionTimeout(int ms) { _connectionTimeout = ms.ToString(); return this; }

    /// <summary>Connection timeout from an expression.</summary>
    public FtpBuilder ConnectionTimeout(IExpression ms) { _connectionTimeout = ms.ToTemplateString(); return this; }

    /// <summary>Operation timeout in milliseconds. Default 60000.</summary>
    public FtpBuilder OperationTimeout(int ms) { _operationTimeout = ms.ToString(); return this; }

    /// <summary>Operation timeout from an expression.</summary>
    public FtpBuilder OperationTimeout(IExpression ms) { _operationTimeout = ms.ToTemplateString(); return this; }

    /// <summary>Use passive mode for data connections. Default true.</summary>
    public FtpBuilder PassiveMode(bool passive = true) { _passiveMode = passive; _passiveModeExplicit = true; return this; }

    /// <summary>Use active mode for data connections.</summary>
    public FtpBuilder ActiveMode() { _passiveMode = false; _passiveModeExplicit = true; return this; }

    // ── TLS ───────────────────────────────────────────────────────────

    /// <summary>Enable FTP over TLS (FTPS).</summary>
    public FtpBuilder UseFtps() { _useFtps = true; return this; }

    /// <summary>Validate server certificate. Default true.</summary>
    public FtpBuilder ValidateCertificate(bool validate = true) { _validateCertificate = validate; return this; }

    // ── Reconnection ──────────────────────────────────────────────────

    /// <summary>Maximum reconnect attempts. Default 3.</summary>
    public FtpBuilder MaximumReconnectAttempts(int attempts) { _maximumReconnectAttempts = attempts.ToString(); return this; }

    /// <summary>Reconnect delay in milliseconds. Default 1000.</summary>
    public FtpBuilder ReconnectDelay(int ms) { _reconnectDelay = ms.ToString(); return this; }

    /// <summary>Disconnect after each operation.</summary>
    public FtpBuilder Disconnect() { _disconnect = true; return this; }

    // ── Consumer ──────────────────────────────────────────────────────

    /// <summary>Poll delay in milliseconds. Default 60000.</summary>
    public FtpBuilder Delay(int ms) { _delay = ms.ToString(); return this; }

    /// <summary>Poll delay from an expression.</summary>
    public FtpBuilder Delay(IExpression ms) { _delay = ms.ToTemplateString(); return this; }

    /// <summary>Initial delay before first poll. Default 1000.</summary>
    public FtpBuilder InitialDelay(int ms) { _initialDelay = ms.ToString(); return this; }

    /// <summary>Initial delay from an expression.</summary>
    public FtpBuilder InitialDelay(IExpression ms) { _initialDelay = ms.ToTemplateString(); return this; }

    /// <summary>File name pattern to include (e.g. "*.csv").</summary>
    public FtpBuilder Include(string pattern) { _include = pattern; return this; }

    /// <summary>File name pattern to exclude.</summary>
    public FtpBuilder Exclude(string pattern) { _exclude = pattern; return this; }

    /// <summary>Recurse into subdirectories.</summary>
    public FtpBuilder Recursive() { _recursive = true; return this; }

    /// <summary>Maximum recursion depth. 0 = unlimited.</summary>
    public FtpBuilder MaxDepth(int depth) { _maxDepth = depth.ToString(); return this; }

    /// <summary>Minimum recursion depth before yielding files.</summary>
    public FtpBuilder MinDepth(int depth) { _minDepth = depth.ToString(); return this; }

    /// <summary>Sort order: None, Name, NameDesc, Modified, ModifiedDesc, Size, SizeDesc.</summary>
    public FtpBuilder SortBy(string sort) { _sortBy = sort; return this; }

    /// <summary>Max files per poll. 0 = unlimited.</summary>
    public FtpBuilder MaxMessagesPerPoll(int max) { _maxMessagesPerPoll = max.ToString(); return this; }

    /// <summary>Minimum file age in milliseconds before processing.</summary>
    public FtpBuilder MinAge(long ms) { _minAge = ms.ToString(); return this; }

    /// <summary>Maximum file age in milliseconds.</summary>
    public FtpBuilder MaxAge(long ms) { _maxAge = ms.ToString(); return this; }

    // ── Post-processing ───────────────────────────────────────────────

    /// <summary>Do nothing after processing (leave file in place).</summary>
    public FtpBuilder Noop() { _noop = true; return this; }

    /// <summary>Delete file after processing.</summary>
    public FtpBuilder Delete() { _delete = true; return this; }

    /// <summary>Move processed file to this directory.</summary>
    public FtpBuilder MoveTo(IExpression directory) { _moveTo = directory.ToTemplateString(); return this; }

    /// <summary>Move processed file to this directory (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder MoveTo(string directory) => MoveTo(new StringExpression(directory));

    /// <summary>Strategy when file exists in move target: Override, Append, Fail, Ignore, Move, TryRename.</summary>
    public FtpBuilder MoveExisting(string strategy) { _moveExisting = strategy; return this; }

    /// <summary>Pre-move directory (temporary move before processing).</summary>
    public FtpBuilder PreMove(IExpression directory) { _preMove = directory.ToTemplateString(); return this; }

    /// <summary>Pre-move directory (temporary move before processing) (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder PreMove(string directory) => PreMove(new StringExpression(directory));

    /// <summary>Directory for files that failed processing.</summary>
    public FtpBuilder MoveFailed(IExpression directory) { _moveFailed = directory.ToTemplateString(); return this; }

    /// <summary>Directory for files that failed processing (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder MoveFailed(string directory) => MoveFailed(new StringExpression(directory));

    // ── Idempotency ───────────────────────────────────────────────────

    /// <summary>Enable idempotent consumer with optional key expression.</summary>
    public FtpBuilder Idempotent(IExpression? key = null) { _idempotent = true; _idempotentKey = key?.ToTemplateString(); return this; }

    /// <summary>Enable idempotent consumer with a key (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder Idempotent(string key) => Idempotent(new StringExpression(key));

    /// <summary>Done file name pattern.</summary>
    public FtpBuilder DoneFileName(IExpression name) { _doneFileName = name.ToTemplateString(); return this; }

    /// <summary>Done file name pattern (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder DoneFileName(string name) => DoneFileName(new StringExpression(name));

    // ── Transfer ──────────────────────────────────────────────────────

    /// <summary>Transfer type: Binary or Ascii.</summary>
    public FtpBuilder TransferType(string type) { _transferType = type; return this; }

    /// <summary>Set exchange body to stream instead of byte[] (consumer only).</summary>
    public FtpBuilder StreamBody() { _streamBody = true; return this; }

    /// <summary>Character encoding for text mode. Default "utf-8".</summary>
    public FtpBuilder Charset(string charset) { _charset = charset; return this; }

    /// <summary>Ignore file-not-found or permission errors.</summary>
    public FtpBuilder IgnoreFileNotFoundOrPermissionError() { _ignoreFileNotFoundOrPermissionError = true; return this; }

    /// <summary>Starting directory must exist. Default true.</summary>
    public FtpBuilder StartingDirectoryMustExist(bool must = true) { _startingDirectoryMustExist = must; return this; }

    /// <summary>Send empty message when poll finds no files.</summary>
    public FtpBuilder SendEmptyMessageWhenIdle() { _sendEmptyMessageWhenIdle = true; return this; }

    // ── Producer ──────────────────────────────────────────────────────

    /// <summary>Output file name or expression.</summary>
    public FtpBuilder FileName(IExpression name) { _fileName = name.ToTemplateString(); return this; }

    /// <summary>Output file name (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder FileName(string name) => FileName(new StringExpression(name));

    /// <summary>Strategy when target file exists: Override, Append, Fail, Ignore, Move, TryRename.</summary>
    public FtpBuilder FileExist(string strategy) { _fileExist = strategy; return this; }

    /// <summary>Strategy for moving existing files: Backup, Timestamp, Guid.</summary>
    public FtpBuilder MoveExistingFileStrategy(string strategy) { _moveExistingFileStrategy = strategy; return this; }

    /// <summary>Temporary file prefix during write.</summary>
    public FtpBuilder TempPrefix(IExpression prefix) { _tempPrefix = prefix.ToTemplateString(); return this; }

    /// <summary>Temporary file prefix during write (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder TempPrefix(string prefix) => TempPrefix(new StringExpression(prefix));

    /// <summary>Temporary file name during write.</summary>
    public FtpBuilder TempFileName(IExpression name) { _tempFileName = name.ToTemplateString(); return this; }

    /// <summary>Temporary file name during write (template string, supports <c>${...}</c>).</summary>
    public FtpBuilder TempFileName(string name) => TempFileName(new StringExpression(name));

    /// <summary>Auto-create target directory. Default true.</summary>
    public FtpBuilder AutoCreate(bool autoCreate = true) { _autoCreate = autoCreate; return this; }

    /// <summary>Allow sending null body.</summary>
    public FtpBuilder AllowNullBody() { _allowNullBody = true; return this; }

    /// <summary>Eagerly delete target file before writing. Default true.</summary>
    public FtpBuilder EagerDeleteTargetFile(bool eager = true) { _eagerDeleteTargetFile = eager; return this; }

    /// <summary>Flatten directory structure (single directory).</summary>
    public FtpBuilder Flatten() { _flatten = true; return this; }

    /// <summary>Jail operations within the starting directory. Default true.</summary>
    public FtpBuilder JailStartingDirectory(bool jail = true) { _jailStartingDirectory = jail; return this; }

    /// <summary>Characters to append after writing.</summary>
    public FtpBuilder AppendChars(string chars) { _appendChars = chars; return this; }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the FTP endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("ftp:///");
        sb.Append(_remotePath.TrimStart('/'));

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(Uri.EscapeDataString(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (!string.IsNullOrEmpty(value)) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value)
        {
            if (value.HasValue) Append(key, value.Value.ToString().ToLowerInvariant());
        }

        // Connection
        AppendIf("host", _host);
        AppendIf("port", _port);
        AppendIf("username", _username);
        AppendIf("password", _password);
        AppendIf("connectionFactory", _connectionFactory);
        AppendIf("connectionTimeout", _connectionTimeout);
        AppendIf("operationTimeout", _operationTimeout);
        if (_passiveModeExplicit) Append("passiveMode", _passiveMode.ToString().ToLowerInvariant());

        // TLS
        AppendBool("useFtps", _useFtps);
        AppendBoolExplicit("validateCertificate", _validateCertificate);

        // Transfer
        AppendIf("transferType", _transferType);
        AppendBool("streamBody", _streamBody);
        AppendIf("charset", _charset);
        AppendBool("ignoreFileNotFoundOrPermissionError", _ignoreFileNotFoundOrPermissionError);
        AppendBoolExplicit("startingDirectoryMustExist", _startingDirectoryMustExist);
        AppendBool("sendEmptyMessageWhenIdle", _sendEmptyMessageWhenIdle);

        // Reconnection
        AppendIf("maximumReconnectAttempts", _maximumReconnectAttempts);
        AppendIf("reconnectDelay", _reconnectDelay);
        AppendBool("disconnect", _disconnect);

        // Consumer
        AppendIf("delay", _delay);
        AppendIf("initialDelay", _initialDelay);
        AppendIf("include", _include);
        AppendIf("exclude", _exclude);
        AppendBool("recursive", _recursive);
        AppendIf("maxDepth", _maxDepth);
        AppendIf("minDepth", _minDepth);
        AppendIf("sortBy", _sortBy);
        AppendIf("maxMessagesPerPoll", _maxMessagesPerPoll);
        AppendIf("minAge", _minAge);
        AppendIf("maxAge", _maxAge);

        // Post-processing
        AppendBool("noop", _noop);
        AppendBool("delete", _delete);
        AppendIf("moveTo", _moveTo);
        AppendIf("moveExisting", _moveExisting);
        AppendIf("preMove", _preMove);
        AppendIf("moveFailed", _moveFailed);

        // Idempotency
        AppendBool("idempotent", _idempotent);
        AppendIf("idempotentKey", _idempotentKey);
        AppendIf("doneFileName", _doneFileName);

        // Producer
        AppendIf("fileName", _fileName);
        AppendIf("fileExist", _fileExist);
        AppendIf("moveExistingFileStrategy", _moveExistingFileStrategy);
        AppendIf("tempPrefix", _tempPrefix);
        AppendIf("tempFileName", _tempFileName);
        AppendBoolExplicit("autoCreate", _autoCreate);
        AppendBool("allowNullBody", _allowNullBody);
        AppendBoolExplicit("eagerDeleteTargetFile", _eagerDeleteTargetFile);
        AppendBool("flatten", _flatten);
        AppendBoolExplicit("jailStartingDirectory", _jailStartingDirectory);
        AppendIf("appendChars", _appendChars);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(FtpBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
