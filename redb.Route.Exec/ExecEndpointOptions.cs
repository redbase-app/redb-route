using redb.Route.Core;

namespace redb.Route.Exec;

/// <summary>
/// Options for the exec endpoint. Shared by producer and consumer.
/// <para>
/// URI: <c>exec://run?command=git&amp;args=status&amp;timeoutMs=5000&amp;allowedCommands=git,ls,pwd</c>
/// </para>
/// </summary>
public sealed class ExecEndpointOptions : EndpointOptions
{
    // ── Command target ──────────────────────────────────────────────

    /// <summary>
    /// The command to execute. May be overridden per-call by the
    /// <see cref="ExecHeaders.Command"/> header or by a JSON body's <c>command</c> field.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Default argument list, whitespace-separated. May be overridden per-call by the
    /// <see cref="ExecHeaders.Args"/> header or by a JSON body's <c>args</c> array.
    /// </summary>
    public string? Args { get; set; }

    // ── Allowlist ───────────────────────────────────────────────────

    /// <summary>
    /// Comma-separated list of permitted command names. When non-empty, every request must
    /// resolve to one of these commands (matched against the executable file name without
    /// extension). When empty, all commands are allowed — the framework defers to the host
    /// OS for permissions. For LLM-tool exposure, set this explicitly.
    /// </summary>
    public string? AllowedCommands { get; set; }

    // ── Process environment ─────────────────────────────────────────

    /// <summary>Working directory for the spawned process (default: the current process cwd).</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Comma-separated <c>KEY=VALUE</c> environment overrides. These are added on top of
    /// the host process environment and override any keys with the same name.
    /// </summary>
    public string? EnvironmentOverrides { get; set; }

    /// <summary>
    /// When <c>true</c>, the spawned process inherits no environment variables — only the
    /// overrides supplied via <see cref="EnvironmentOverrides"/> are passed in. Default <c>false</c>.
    /// </summary>
    public bool ScrubEnvironment { get; set; }

    // ── Limits ──────────────────────────────────────────────────────

    /// <summary>
    /// Wall-clock timeout in milliseconds. The process is forcibly terminated when exceeded
    /// and the output exchange is flagged via <see cref="ExecHeaders.TimedOut"/>. Default 30000.
    /// 0 = no timeout.
    /// </summary>
    public int TimeoutMs { get; set; } = 30_000;

    /// <summary>Maximum bytes captured from stdout. Excess output is silently dropped. Default 1 MiB.</summary>
    public int MaxStdoutBytes { get; set; } = 1024 * 1024;

    /// <summary>Maximum bytes captured from stderr. Excess output is silently dropped. Default 256 KiB.</summary>
    public int MaxStderrBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// When <c>true</c>, stderr is folded into the JSON response body alongside stdout.
    /// When <c>false</c>, only stdout is set as the body and stderr is exposed via headers
    /// (<see cref="ExecHeaders.Stderr"/>) only. Default <c>true</c>.
    /// </summary>
    public bool CaptureStderrInBody { get; set; } = true;

    // ── Output shape ────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c> (default), <see cref="redb.Route.Abstractions.IExchange.Out"/> is set to a
    /// JSON object string of the form <c>{"stdout":"...","stderr":"...","exitCode":0}</c> — the
    /// shape required by the LLM tool ABI. When <c>false</c>, only stdout is written as a plain
    /// string body and metadata is exposed exclusively through headers.
    /// </summary>
    public bool JsonResponse { get; set; } = true;

    // ── Consumer (scheduled) ────────────────────────────────────────

    /// <summary>
    /// Schedule expression for consumer mode. Supported formats: <c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>1h</c>.
    /// Cron expressions are not supported by the inline consumer — use <c>From("quartz://...")</c> for cron.
    /// </summary>
    public string? Schedule { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (TimeoutMs < 0)
            throw new ArgumentException("TimeoutMs must be >= 0 (0 = unlimited).", nameof(TimeoutMs));

        if (MaxStdoutBytes <= 0)
            throw new ArgumentException("MaxStdoutBytes must be > 0.", nameof(MaxStdoutBytes));

        if (MaxStderrBytes <= 0)
            throw new ArgumentException("MaxStderrBytes must be > 0.", nameof(MaxStderrBytes));
    }
}
