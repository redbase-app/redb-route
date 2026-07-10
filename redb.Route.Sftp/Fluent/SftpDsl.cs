using System.Text;
using System.Web;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Sftp;

/// <summary>
/// Fluent API for SFTP endpoints.
/// <example><code>
/// .From(Sftp.Directory("/incoming").Host("sftp.example.com").Username("admin").Password("secret")
///     .Include("*.csv").Recursive().Delete())
/// .To(Sftp.Directory("/outgoing").Host("sftp.example.com").Username("admin").Password("secret")
///     .AutoCreate())
/// </code></example>
/// </summary>
public static class Sftp
{
    /// <summary>Creates an SFTP endpoint for the given remote directory path.</summary>
    public static SftpBuilder Directory(string remotePath) => new(remotePath);
}

/// <summary>
/// Fluent builder for SFTP endpoint URIs.
/// </summary>
public sealed class SftpBuilder
{
    private readonly string _remotePath;

    // Connection
    private string? _host;
    private string? _port;
    private string? _username;
    private string? _password;
    private string? _privateKeyPath;
    private string? _privateKeyPassphrase;
    private bool _useKeyboardInteractive;
    private string? _preferredAuthentications;
    private string? _serverFingerprint;
    private bool _strictHostKeyChecking;
    private string? _knownHostsFile;
    private string? _connectionTimeout;
    private string? _operationTimeout;
    private string? _keepAliveInterval;
    private string? _bufferSize;
    private bool _compression;

    // Proxy
    private string? _proxyType;
    private string? _proxyHost;
    private string? _proxyPort;
    private string? _proxyUsername;
    private string? _proxyPassword;

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

    // Transfer
    private bool? _binary;
    private bool _streamBody;
    private string? _charset;
    private bool? _stepWise;
    private string? _separator;
    private bool _ignoreFileNotFoundOrPermissionError;
    private bool? _startingDirectoryMustExist;
    private bool _directoryMustExist;
    private bool _sendEmptyMessageWhenIdle;

    // Producer
    private string? _fileName;
    private string? _fileExist;
    private string? _moveExistingFileStrategy;
    private string? _tempPrefix;
    private string? _tempFileName;
    private string? _chmod;
    private string? _chmodDirectory;
    private bool? _autoCreate;
    private bool _allowNullBody;
    private bool? _eagerDeleteTargetFile;
    private bool _keepLastModified;
    private bool _flatten;
    private bool? _jailStartingDirectory;
    private string? _appendChars;

    internal SftpBuilder(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        _remotePath = remotePath;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>SFTP server hostname. Default "localhost".</summary>
    public SftpBuilder Host(IExpression host) { _host = host.ToTemplateString(); return this; }
    /// <summary>SFTP server hostname (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder Host(string host) => Host(new StringExpression(host));

    /// <summary>SFTP server port. Default 22.</summary>
    public SftpBuilder Port(int port) { _port = port.ToString(); return this; }
    /// <summary>SFTP server port from an expression.</summary>
    public SftpBuilder Port(IExpression port) { _port = port.ToTemplateString(); return this; }

    /// <summary>Username for authentication.</summary>
    public SftpBuilder Username(IExpression username) { _username = username.ToTemplateString(); return this; }
    /// <summary>Username for authentication (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder Username(string username) => Username(new StringExpression(username));

    /// <summary>Password for authentication.</summary>
    public SftpBuilder Password(IExpression password) { _password = password.ToTemplateString(); return this; }
    /// <summary>Password for authentication (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder Password(string password) => Password(new StringExpression(password));

    /// <summary>Path to private key file.</summary>
    public SftpBuilder PrivateKey(IExpression path, IExpression? passphrase = null)
    {
        _privateKeyPath = path.ToTemplateString(); _privateKeyPassphrase = passphrase?.ToTemplateString(); return this;
    }

    /// <summary>Enable keyboard-interactive authentication.</summary>
    public SftpBuilder UseKeyboardInteractive() { _useKeyboardInteractive = true; return this; }

    /// <summary>Preferred authentication methods, comma-separated.</summary>
    public SftpBuilder PreferredAuthentications(string methods) { _preferredAuthentications = methods; return this; }

    /// <summary>Expected server host key fingerprint.</summary>
    public SftpBuilder ServerFingerprint(IExpression fingerprint) { _serverFingerprint = fingerprint.ToTemplateString(); return this; }
    /// <summary>Expected server host key fingerprint (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder ServerFingerprint(string fingerprint) => ServerFingerprint(new StringExpression(fingerprint));

    /// <summary>Enable strict host key checking.</summary>
    public SftpBuilder StrictHostKeyChecking() { _strictHostKeyChecking = true; return this; }

    /// <summary>Path to known_hosts file.</summary>
    public SftpBuilder KnownHostsFile(string path) { _knownHostsFile = path; return this; }

    /// <summary>Connection timeout in milliseconds. Default 30000.</summary>
    public SftpBuilder ConnectionTimeout(int ms) { _connectionTimeout = ms.ToString(); return this; }
    /// <summary>Connection timeout from an expression.</summary>
    public SftpBuilder ConnectionTimeout(IExpression ms) { _connectionTimeout = ms.ToTemplateString(); return this; }

    /// <summary>Operation timeout in milliseconds. Default 60000.</summary>
    public SftpBuilder OperationTimeout(int ms) { _operationTimeout = ms.ToString(); return this; }
    /// <summary>Operation timeout from an expression.</summary>
    public SftpBuilder OperationTimeout(IExpression ms) { _operationTimeout = ms.ToTemplateString(); return this; }

    /// <summary>Keep-alive interval in milliseconds. 0 = disabled.</summary>
    public SftpBuilder KeepAliveInterval(int ms) { _keepAliveInterval = ms.ToString(); return this; }
    /// <summary>Keep-alive interval from an expression.</summary>
    public SftpBuilder KeepAliveInterval(IExpression ms) { _keepAliveInterval = ms.ToTemplateString(); return this; }

    /// <summary>Buffer size for transfers. Default 32768.</summary>
    public SftpBuilder BufferSize(int bytes) { _bufferSize = bytes.ToString(); return this; }
    /// <summary>Buffer size from an expression.</summary>
    public SftpBuilder BufferSize(IExpression bytes) { _bufferSize = bytes.ToTemplateString(); return this; }

    /// <summary>Enable SSH compression.</summary>
    public SftpBuilder Compression() { _compression = true; return this; }

    // ── Proxy ─────────────────────────────────────────────────────────

    /// <summary>Proxy configuration: type (None, Socks4, Socks5, Http), host, and port.</summary>
    public SftpBuilder Proxy(string type, IExpression host, int port = 1080)
    {
        _proxyType = type; _proxyHost = host.ToTemplateString(); _proxyPort = port.ToString(); return this;
    }

    /// <summary>Proxy authentication.</summary>
    public SftpBuilder ProxyAuth(IExpression username, IExpression password)
    {
        _proxyUsername = username.ToTemplateString(); _proxyPassword = password.ToTemplateString(); return this;
    }

    // ── Reconnection ──────────────────────────────────────────────────

    /// <summary>Maximum reconnect attempts. Default 3.</summary>
    public SftpBuilder MaximumReconnectAttempts(int attempts) { _maximumReconnectAttempts = attempts.ToString(); return this; }
    /// <summary>Maximum reconnect attempts from an expression.</summary>
    public SftpBuilder MaximumReconnectAttempts(IExpression attempts) { _maximumReconnectAttempts = attempts.ToTemplateString(); return this; }

    /// <summary>Reconnect delay in milliseconds. Default 1000.</summary>
    public SftpBuilder ReconnectDelay(int ms) { _reconnectDelay = ms.ToString(); return this; }
    /// <summary>Reconnect delay from an expression.</summary>
    public SftpBuilder ReconnectDelay(IExpression ms) { _reconnectDelay = ms.ToTemplateString(); return this; }

    /// <summary>Disconnect after each operation.</summary>
    public SftpBuilder Disconnect() { _disconnect = true; return this; }

    // ── Consumer ──────────────────────────────────────────────────────

    /// <summary>Poll delay in milliseconds. Default 60000.</summary>
    public SftpBuilder Delay(int ms) { _delay = ms.ToString(); return this; }
    /// <summary>Poll delay from an expression.</summary>
    public SftpBuilder Delay(IExpression ms) { _delay = ms.ToTemplateString(); return this; }

    /// <summary>Initial delay before first poll. Default 1000.</summary>
    public SftpBuilder InitialDelay(int ms) { _initialDelay = ms.ToString(); return this; }
    /// <summary>Initial delay from an expression.</summary>
    public SftpBuilder InitialDelay(IExpression ms) { _initialDelay = ms.ToTemplateString(); return this; }

    /// <summary>File name pattern to include (e.g. "*.csv").</summary>
    public SftpBuilder Include(string pattern) { _include = pattern; return this; }

    /// <summary>File name pattern to exclude.</summary>
    public SftpBuilder Exclude(string pattern) { _exclude = pattern; return this; }

    /// <summary>Recurse into subdirectories.</summary>
    public SftpBuilder Recursive() { _recursive = true; return this; }

    /// <summary>Maximum recursion depth. 0 = unlimited.</summary>
    public SftpBuilder MaxDepth(int depth) { _maxDepth = depth.ToString(); return this; }
    /// <summary>Maximum recursion depth from an expression.</summary>
    public SftpBuilder MaxDepth(IExpression depth) { _maxDepth = depth.ToTemplateString(); return this; }

    /// <summary>Minimum recursion depth before yielding files.</summary>
    public SftpBuilder MinDepth(int depth) { _minDepth = depth.ToString(); return this; }
    /// <summary>Minimum recursion depth from an expression.</summary>
    public SftpBuilder MinDepth(IExpression depth) { _minDepth = depth.ToTemplateString(); return this; }

    /// <summary>Sort order: None, Name, NameDesc, Modified, ModifiedDesc, Size, SizeDesc.</summary>
    public SftpBuilder SortBy(string sort) { _sortBy = sort; return this; }

    /// <summary>Max files per poll. 0 = unlimited.</summary>
    public SftpBuilder MaxMessagesPerPoll(int max) { _maxMessagesPerPoll = max.ToString(); return this; }
    /// <summary>Max files per poll from an expression.</summary>
    public SftpBuilder MaxMessagesPerPoll(IExpression max) { _maxMessagesPerPoll = max.ToTemplateString(); return this; }

    /// <summary>Minimum file age in milliseconds before processing.</summary>
    public SftpBuilder MinAge(long ms) { _minAge = ms.ToString(); return this; }
    /// <summary>Minimum file age from an expression.</summary>
    public SftpBuilder MinAge(IExpression ms) { _minAge = ms.ToTemplateString(); return this; }

    /// <summary>Maximum file age in milliseconds.</summary>
    public SftpBuilder MaxAge(long ms) { _maxAge = ms.ToString(); return this; }
    /// <summary>Maximum file age from an expression.</summary>
    public SftpBuilder MaxAge(IExpression ms) { _maxAge = ms.ToTemplateString(); return this; }

    // ── Post-processing ───────────────────────────────────────────────

    /// <summary>Do nothing after processing (leave file in place).</summary>
    public SftpBuilder Noop() { _noop = true; return this; }

    /// <summary>Delete file after processing.</summary>
    public SftpBuilder Delete() { _delete = true; return this; }

    /// <summary>Move processed file to this directory.</summary>
    public SftpBuilder MoveTo(IExpression directory) { _moveTo = directory.ToTemplateString(); return this; }
    /// <summary>Move processed file to this directory (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder MoveTo(string directory) => MoveTo(new StringExpression(directory));

    /// <summary>Strategy when file exists in move target: Override, Append, Fail, Ignore, Move, TryRename.</summary>
    public SftpBuilder MoveExisting(string strategy) { _moveExisting = strategy; return this; }

    /// <summary>Pre-move directory (temporary move before processing).</summary>
    public SftpBuilder PreMove(IExpression directory) { _preMove = directory.ToTemplateString(); return this; }
    /// <summary>Pre-move directory (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder PreMove(string directory) => PreMove(new StringExpression(directory));

    /// <summary>Directory for files that failed processing.</summary>
    public SftpBuilder MoveFailed(IExpression directory) { _moveFailed = directory.ToTemplateString(); return this; }
    /// <summary>Directory for files that failed processing (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder MoveFailed(string directory) => MoveFailed(new StringExpression(directory));

    // ── Idempotency ───────────────────────────────────────────────────

    /// <summary>Enable idempotent consumer with optional key expression.</summary>
    public SftpBuilder Idempotent(IExpression? key = null) { _idempotent = true; _idempotentKey = key?.ToTemplateString(); return this; }
    /// <summary>Enable idempotent consumer with a key (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder Idempotent(string key) => Idempotent(new StringExpression(key));

    /// <summary>Done file name pattern.</summary>
    public SftpBuilder DoneFileName(IExpression name) { _doneFileName = name.ToTemplateString(); return this; }
    /// <summary>Done file name pattern (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder DoneFileName(string name) => DoneFileName(new StringExpression(name));

    // ── Transfer ──────────────────────────────────────────────────────

    /// <summary>Binary transfer mode. Default true.</summary>
    public SftpBuilder Binary(bool binary = true) { _binary = binary; return this; }

    /// <summary>Set exchange body to SftpFileStream instead of byte[] (consumer only).</summary>
    public SftpBuilder StreamBody() { _streamBody = true; return this; }

    /// <summary>Character encoding for text mode. Default "utf-8".</summary>
    public SftpBuilder Charset(string charset) { _charset = charset; return this; }

    /// <summary>Step-wise (cd into each directory). Default true.</summary>
    public SftpBuilder StepWise(bool stepWise = true) { _stepWise = stepWise; return this; }

    /// <summary>Path separator: Auto, Unix, Windows.</summary>
    public SftpBuilder Separator(string sep) { _separator = sep; return this; }

    /// <summary>Ignore file-not-found or permission errors.</summary>
    public SftpBuilder IgnoreFileNotFoundOrPermissionError() { _ignoreFileNotFoundOrPermissionError = true; return this; }

    /// <summary>Starting directory must exist. Default true.</summary>
    public SftpBuilder StartingDirectoryMustExist(bool must = true) { _startingDirectoryMustExist = must; return this; }

    /// <summary>Target directory must exist (producer).</summary>
    public SftpBuilder DirectoryMustExist() { _directoryMustExist = true; return this; }

    /// <summary>Send empty message when poll finds no files.</summary>
    public SftpBuilder SendEmptyMessageWhenIdle() { _sendEmptyMessageWhenIdle = true; return this; }

    // ── Producer ──────────────────────────────────────────────────────

    /// <summary>Output file name or expression (e.g. <c>Header("CamelFileName")</c>).</summary>
    public SftpBuilder FileName(IExpression name) { _fileName = name.ToTemplateString(); return this; }
    /// <summary>Output file name (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder FileName(string name) => FileName(new StringExpression(name));

    /// <summary>Strategy when target file exists: Override, Append, Fail, Ignore, Move, TryRename.</summary>
    public SftpBuilder FileExist(string strategy) { _fileExist = strategy; return this; }

    /// <summary>Strategy for moving existing files: Backup, Timestamp, Guid.</summary>
    public SftpBuilder MoveExistingFileStrategy(string strategy) { _moveExistingFileStrategy = strategy; return this; }

    /// <summary>Temporary file prefix during write.</summary>
    public SftpBuilder TempPrefix(IExpression prefix) { _tempPrefix = prefix.ToTemplateString(); return this; }
    /// <summary>Temporary file prefix during write (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder TempPrefix(string prefix) => TempPrefix(new StringExpression(prefix));

    /// <summary>Temporary file name during write.</summary>
    public SftpBuilder TempFileName(IExpression name) { _tempFileName = name.ToTemplateString(); return this; }
    /// <summary>Temporary file name during write (template string, supports <c>${...}</c>).</summary>
    public SftpBuilder TempFileName(string name) => TempFileName(new StringExpression(name));

    /// <summary>File permissions (e.g. "644").</summary>
    public SftpBuilder Chmod(string permissions) { _chmod = permissions; return this; }

    /// <summary>Directory permissions (e.g. "755").</summary>
    public SftpBuilder ChmodDirectory(string permissions) { _chmodDirectory = permissions; return this; }

    /// <summary>Auto-create target directory. Default true.</summary>
    public SftpBuilder AutoCreate(bool autoCreate = true) { _autoCreate = autoCreate; return this; }

    /// <summary>Allow sending null body.</summary>
    public SftpBuilder AllowNullBody() { _allowNullBody = true; return this; }

    /// <summary>Eagerly delete target file before writing. Default true.</summary>
    public SftpBuilder EagerDeleteTargetFile(bool eager = true) { _eagerDeleteTargetFile = eager; return this; }

    /// <summary>Preserve last modified timestamp.</summary>
    public SftpBuilder KeepLastModified() { _keepLastModified = true; return this; }

    /// <summary>Flatten directory structure (single directory).</summary>
    public SftpBuilder Flatten() { _flatten = true; return this; }

    /// <summary>Jail operations within the starting directory. Default true.</summary>
    public SftpBuilder JailStartingDirectory(bool jail = true) { _jailStartingDirectory = jail; return this; }

    /// <summary>Characters to append after writing.</summary>
    public SftpBuilder AppendChars(string chars) { _appendChars = chars; return this; }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the SFTP endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("sftp:///");
        sb.Append(_remotePath.TrimStart('/'));

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

        // Connection
        AppendIf("host", _host);
        AppendIf("port", _port);
        AppendIf("username", _username);
        AppendIf("password", _password);
        AppendIf("privateKeyPath", _privateKeyPath);
        AppendIf("privateKeyPassphrase", _privateKeyPassphrase);
        AppendBool("useKeyboardInteractive", _useKeyboardInteractive);
        AppendIf("preferredAuthentications", _preferredAuthentications);
        AppendIf("serverFingerprint", _serverFingerprint);
        AppendBool("strictHostKeyChecking", _strictHostKeyChecking);
        AppendIf("knownHostsFile", _knownHostsFile);
        AppendIf("connectionTimeout", _connectionTimeout);
        AppendIf("operationTimeout", _operationTimeout);
        AppendIf("keepAliveInterval", _keepAliveInterval);
        AppendIf("bufferSize", _bufferSize);
        AppendBool("compression", _compression);

        // Proxy
        AppendIf("proxyType", _proxyType);
        AppendIf("proxyHost", _proxyHost);
        AppendIf("proxyPort", _proxyPort);
        AppendIf("proxyUsername", _proxyUsername);
        AppendIf("proxyPassword", _proxyPassword);

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

        // Transfer
        AppendBoolExplicit("binary", _binary);
        AppendBool("streamBody", _streamBody);
        AppendIf("charset", _charset);
        AppendBoolExplicit("stepWise", _stepWise);
        AppendIf("separator", _separator);
        AppendBool("ignoreFileNotFoundOrPermissionError", _ignoreFileNotFoundOrPermissionError);
        AppendBoolExplicit("startingDirectoryMustExist", _startingDirectoryMustExist);
        AppendBool("directoryMustExist", _directoryMustExist);
        AppendBool("sendEmptyMessageWhenIdle", _sendEmptyMessageWhenIdle);

        // Producer
        AppendIf("fileName", _fileName);
        AppendIf("fileExist", _fileExist);
        AppendIf("moveExistingFileStrategy", _moveExistingFileStrategy);
        AppendIf("tempPrefix", _tempPrefix);
        AppendIf("tempFileName", _tempFileName);
        AppendIf("chmod", _chmod);
        AppendIf("chmodDirectory", _chmodDirectory);
        AppendBoolExplicit("autoCreate", _autoCreate);
        AppendBool("allowNullBody", _allowNullBody);
        AppendBoolExplicit("eagerDeleteTargetFile", _eagerDeleteTargetFile);
        AppendBool("keepLastModified", _keepLastModified);
        AppendBool("flatten", _flatten);
        AppendBoolExplicit("jailStartingDirectory", _jailStartingDirectory);
        AppendIf("appendChars", _appendChars);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(SftpBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
