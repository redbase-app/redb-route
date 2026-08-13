using System.Text;

namespace redb.Route.Exec;

/// <summary>
/// Fluent entry point for exec endpoints. Build URIs without manual string concatenation.
/// <example><code>
/// // Producer — request/response
/// .To(ExecDsl.Run("git").Args("status").AllowedCommands("git").TimeoutMs(5000))
///
/// // Consumer — scheduled
/// .From(ExecDsl.Run("./health-check.sh").Schedule("5m"))
/// </code></example>
/// </summary>
public static class ExecDsl
{
    /// <summary>Build an exec endpoint URI for the given command. The host part is fixed at <c>run</c>.</summary>
    public static ExecBuilder Run(string? command = null) => new(command);
}

/// <summary>Fluent builder for exec endpoint URIs. Scheme: <c>exec</c>.</summary>
public sealed class ExecBuilder
{
    private string? _command;
    private string? _args;
    private string? _allowedCommands;
    private string? _workingDirectory;
    private string? _envOverrides;
    private bool _scrubEnv;
    private int? _timeoutMs;
    private int? _maxStdoutBytes;
    private int? _maxStderrBytes;
    private bool? _captureStderrInBody;
    private bool? _jsonResponse;
    private string? _schedule;

    internal ExecBuilder(string? command) => _command = command;

    // ── Command target ─────────────────────────────────────────────

    /// <summary>The command to execute (overrides the constructor argument).</summary>
    public ExecBuilder Command(string command) { _command = command; return this; }

    /// <summary>Default argument list (whitespace-separated; quotes preserved).</summary>
    public ExecBuilder Args(params string[] args)
    {
        _args = args.Length == 0 ? null : string.Join(' ', args.Select(QuoteIfNeeded));
        return this;
    }

    // ── Allowlist ──────────────────────────────────────────────────

    /// <summary>Restrict execution to the given command names (file-name-only, case-insensitive).</summary>
    public ExecBuilder AllowedCommands(params string[] commands)
    {
        _allowedCommands = commands.Length == 0 ? null : string.Join(',', commands);
        return this;
    }

    // ── Process environment ────────────────────────────────────────

    /// <summary>Working directory for the spawned process.</summary>
    public ExecBuilder WorkingDirectory(string path) { _workingDirectory = path; return this; }

    /// <summary>Add or override an environment variable.</summary>
    public ExecBuilder EnvOverride(string key, string value)
    {
        var entry = $"{key}={value}";
        _envOverrides = string.IsNullOrEmpty(_envOverrides) ? entry : _envOverrides + "," + entry;
        return this;
    }

    /// <summary>When set, the spawned process inherits no host environment variables.</summary>
    public ExecBuilder ScrubEnvironment(bool value = true) { _scrubEnv = value; return this; }

    // ── Limits ─────────────────────────────────────────────────────

    /// <summary>Wall-clock timeout in milliseconds (0 = unlimited).</summary>
    public ExecBuilder TimeoutMs(int ms) { _timeoutMs = ms; return this; }

    /// <summary>Cap stdout capture (bytes).</summary>
    public ExecBuilder MaxStdoutBytes(int bytes) { _maxStdoutBytes = bytes; return this; }

    /// <summary>Cap stderr capture (bytes).</summary>
    public ExecBuilder MaxStderrBytes(int bytes) { _maxStderrBytes = bytes; return this; }

    /// <summary>Whether to fold stderr into the body (default true).</summary>
    public ExecBuilder CaptureStderrInBody(bool value = true) { _captureStderrInBody = value; return this; }

    // ── Output shape ───────────────────────────────────────────────

    /// <summary>Whether the outbound body is a JSON object (default true).</summary>
    public ExecBuilder JsonResponse(bool value = true) { _jsonResponse = value; return this; }

    // ── Scheduled consumer ─────────────────────────────────────────

    /// <summary>Schedule the consumer to fire on a fixed interval (e.g. <c>"30s"</c>, <c>"5m"</c>).</summary>
    public ExecBuilder Schedule(string schedule) { _schedule = schedule; return this; }

    // ── Build ──────────────────────────────────────────────────────

    /// <summary>Build the URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder("exec://run");

        var sep = '?';
        void Append(string key, string v)
        {
            sb.Append(sep);
            sb.Append(key);
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(v));
            sep = '&';
        }
        void AppendStr(string key, string? v) { if (!string.IsNullOrEmpty(v)) Append(key, v); }
        void AppendInt(string key, int? v) { if (v.HasValue) Append(key, v.Value.ToString()); }
        void AppendBool(string key, bool v) { if (v) Append(key, "true"); }
        void AppendBoolN(string key, bool? v) { if (v.HasValue) Append(key, v.Value.ToString().ToLowerInvariant()); }

        AppendStr("command", _command);
        AppendStr("args", _args);
        AppendStr("allowedCommands", _allowedCommands);
        AppendStr("workingDirectory", _workingDirectory);
        AppendStr("environmentOverrides", _envOverrides);
        AppendBool("scrubEnvironment", _scrubEnv);
        AppendInt("timeoutMs", _timeoutMs);
        AppendInt("maxStdoutBytes", _maxStdoutBytes);
        AppendInt("maxStderrBytes", _maxStderrBytes);
        AppendBoolN("captureStderrInBody", _captureStderrInBody);
        AppendBoolN("jsonResponse", _jsonResponse);
        AppendStr("schedule", _schedule);

        return sb.ToString();
    }

    private static string QuoteIfNeeded(string a)
        => a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a;

    /// <summary>Implicit cast to string — use the builder anywhere a URI string is accepted.</summary>
    public static implicit operator string(ExecBuilder b) => b.Build();

    /// <inheritdoc />
    public override string ToString() => Build();
}
