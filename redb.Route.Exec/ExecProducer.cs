using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Exec;

/// <summary>
/// Spawns OS processes synchronously and returns stdout/stderr/exitCode on the outbound exchange.
/// <para>
/// Input resolution order (highest → lowest priority):
/// <list type="number">
///   <item>JSON body of the form <c>{"command":"...","args":["..."]}</c> on <see cref="IExchange.In"/>.</item>
///   <item><see cref="ExecHeaders.Command"/>/<see cref="ExecHeaders.Args"/> headers.</item>
///   <item><see cref="ExecEndpointOptions.Command"/>/<see cref="ExecEndpointOptions.Args"/> URI options.</item>
/// </list>
/// </para>
/// <para>
/// Security: when <see cref="ExecEndpointOptions.AllowedCommands"/> is set, the resolved
/// command must match one of the listed names (case-insensitive, file-name-only). Requests
/// against commands not on the allowlist throw <see cref="UnauthorizedAccessException"/>.
/// </para>
/// </summary>
public sealed class ExecProducer : ConnectableProducer
{
    private readonly ExecEndpoint _endpoint;
    private readonly ExecEndpointOptions _options;
    private readonly HashSet<string>? _allowed;
    private readonly Dictionary<string, string?>? _envOverrides;

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"exec:{_options.Command ?? "<dynamic>"}";

    /// <summary>Creates an exec producer.</summary>
    public ExecProducer(ExecEndpoint endpoint, ExecEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _allowed = ParseAllowed(options.AllowedCommands);
        _envOverrides = ParseEnv(options.EnvironmentOverrides);
    }

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(exchange);

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"exec {_options.Command ?? "<dynamic>"}",
            ActivityKind.Client,
            "process.executable.name", "exec",
            _endpoint.Uri.NormalizedKey);

        var (command, args) = ResolveCommand(exchange);

        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException(
                "exec://: no command resolved. Set the URI 'command' option, the redbExec.Command header, " +
                "or send a JSON body with a \"command\" field.");

        EnsureAllowed(command);

        var psi = BuildStartInfo(command, args);
        // Do NOT log argument values — they routinely carry secrets (tokens, passwords
        // passed as CLI flags). Emit the executable and the argument count only.
        Logger?.LogDebug("exec → {Command} ({ArgCount} args)", psi.FileName, args.Count);

        var result = await RunAsync(psi, ct).ConfigureAwait(false);

        WriteOutput(exchange, command, args, result);
    }

    // ── Resolution ─────────────────────────────────────────────────

    internal (string Command, IReadOnlyList<string> Args) ResolveCommand(IExchange exchange)
    {
        // 1) JSON body
        var body = exchange.In.Body;
        if (body is string s && !string.IsNullOrWhiteSpace(s) && LooksLikeJson(s))
        {
            if (TryParseJson(s, out var jc, out var ja))
                return (jc!, ja);
        }

        // 2) Headers
        if (exchange.In.Headers.TryGetValue(ExecHeaders.Command, out var hc) && hc is string hcs && !string.IsNullOrWhiteSpace(hcs))
        {
            var ha = exchange.In.Headers.TryGetValue(ExecHeaders.Args, out var rawArgs) ? rawArgs : null;
            return (hcs, NormalizeArgs(ha));
        }

        // 3) Options
        return (_options.Command ?? string.Empty, SplitArgs(_options.Args));
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.AsSpan().TrimStart();
        return t.Length > 0 && t[0] == '{';
    }

    private static bool TryParseJson(string json, out string? command, out IReadOnlyList<string> args)
    {
        command = null;
        args = Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
                command = c.GetString();

            if (root.TryGetProperty("args", out var a))
            {
                if (a.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>(a.GetArrayLength());
                    foreach (var el in a.EnumerateArray())
                        list.Add(el.ValueKind == JsonValueKind.String ? el.GetString()! : el.ToString());
                    args = list;
                }
                else if (a.ValueKind == JsonValueKind.String)
                {
                    args = SplitArgs(a.GetString());
                }
            }

            return command is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> NormalizeArgs(object? raw) => raw switch
    {
        null => Array.Empty<string>(),
        string s => SplitArgs(s),
        IEnumerable<string> e => e.ToArray(),
        _ => SplitArgs(raw.ToString())
    };

    private static IReadOnlyList<string> SplitArgs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        // Whitespace split that respects double-quoted spans.
        var list = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var ch in s)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (!inQuote && char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    // ── Allowlist ──────────────────────────────────────────────────

    internal void EnsureAllowed(string command)
    {
        if (_allowed is null || _allowed.Count == 0) return;

        var name = ExtractCommandName(command);
        if (!_allowed.Contains(name))
            throw new UnauthorizedAccessException(
                $"exec://: command '{name}' is not on the allowlist. " +
                $"Allowed: {string.Join(", ", _allowed)}.");
    }

    private static string ExtractCommandName(string command)
    {
        var path = command.Trim().Trim('"');
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) name = path;
        return name.ToLowerInvariant();
    }

    private static HashSet<string>? ParseAllowed(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(part.ToLowerInvariant());
        return set.Count == 0 ? null : set;
    }

    private static Dictionary<string, string?>? ParseEnv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var k = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();
            dict[k] = v;
        }
        return dict.Count == 0 ? null : dict;
    }

    // ── Process spawn ──────────────────────────────────────────────

    private ProcessStartInfo BuildStartInfo(string command, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = _options.WorkingDirectory ?? string.Empty,
            // Decode child output using the host console's active codepage. Without this, .NET
            // defaults to UTF-8 while cmd.exe / wmic / net / fsutil emit OEM bytes (cp866 on RU,
            // cp437 on EN, cp932 on JP, …) — the mismatch shows up as U+FFFD replacement chars.
            StandardOutputEncoding = ConsoleOutputEncoding,
            StandardErrorEncoding  = ConsoleOutputEncoding,
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        if (_options.ScrubEnvironment)
            psi.Environment.Clear();

        if (_envOverrides is not null)
        {
            foreach (var (k, v) in _envOverrides)
                psi.Environment[k] = v;
        }

        return psi;
    }

    /// <summary>
    /// Encoding used to decode the child process's stdout/stderr. On Windows this is the
    /// active console codepage (OEM); elsewhere UTF-8. Computed once per process.
    /// </summary>
    private static readonly Encoding ConsoleOutputEncoding = ResolveConsoleOutputEncoding();

    private static Encoding ResolveConsoleOutputEncoding()
    {
        if (!OperatingSystem.IsWindows())
            return Encoding.UTF8;

        // CodePagesEncodingProvider gives us cp866/cp1251/etc. on .NET (which only ships
        // ASCII/UTF-8/UTF-16/UTF-32 in the BCL).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try { return Encoding.GetEncoding(GetOEMCP()); }
        catch { return Console.OutputEncoding; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetOEMCP();

    private async Task<ExecResult> RunAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdoutCap = new BoundedBuffer(_options.MaxStdoutBytes);
        var stderrCap = new BoundedBuffer(_options.MaxStderrBytes);

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutCap.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrCap.AppendLine(e.Data); };

        var sw = Stopwatch.StartNew();

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{psi.FileName}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        try
        {
            using var timeoutCts = _options.TimeoutMs > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : CancellationTokenSource.CreateLinkedTokenSource(ct);

            if (_options.TimeoutMs > 0)
                timeoutCts.CancelAfter(_options.TimeoutMs);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                TryKill(process);
                if (ct.IsCancellationRequested) throw;
            }
        }
        finally
        {
            sw.Stop();
        }

        // Drain any pending async-stream data.
        process.WaitForExit();

        return new ExecResult(
            ExitCode: timedOut ? -1 : process.ExitCode,
            Stdout: stdoutCap.ToString(),
            Stderr: stderrCap.ToString(),
            StdoutBytes: stdoutCap.WrittenBytes,
            StderrBytes: stderrCap.WrittenBytes,
            DurationMs: sw.ElapsedMilliseconds,
            TimedOut: timedOut);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    // ── Output ─────────────────────────────────────────────────────

    private void WriteOutput(IExchange exchange, string command, IReadOnlyList<string> args, ExecResult r)
    {
        exchange.Out ??= exchange.In.Clone();

        var commandLine = command + (args.Count > 0 ? " " + string.Join(' ', args) : string.Empty);

        if (_options.JsonResponse)
        {
            // JSON body — the shape an LLM tool will deserialise.
            var payload = _options.CaptureStderrInBody
                ? new { stdout = r.Stdout, stderr = r.Stderr, exitCode = r.ExitCode, timedOut = r.TimedOut }
                : (object)new { stdout = r.Stdout, exitCode = r.ExitCode, timedOut = r.TimedOut };
            exchange.Out.Body = JsonSerializer.Serialize(payload);
            exchange.Out.ContentType = "application/json";
        }
        else
        {
            exchange.Out.Body = _options.CaptureStderrInBody && r.Stderr.Length > 0
                ? r.Stdout + r.Stderr
                : r.Stdout;
            exchange.Out.ContentType = "text/plain";
        }

        exchange.Out.Headers[ExecHeaders.ExitCode] = r.ExitCode;
        exchange.Out.Headers[ExecHeaders.Stdout] = r.Stdout;
        exchange.Out.Headers[ExecHeaders.Stderr] = r.Stderr;
        exchange.Out.Headers[ExecHeaders.StdoutBytes] = r.StdoutBytes;
        exchange.Out.Headers[ExecHeaders.StderrBytes] = r.StderrBytes;
        exchange.Out.Headers[ExecHeaders.DurationMs] = r.DurationMs;
        exchange.Out.Headers[ExecHeaders.TimedOut] = r.TimedOut;
        exchange.Out.Headers[ExecHeaders.CommandLine] = commandLine;
    }

    private readonly record struct ExecResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        long StdoutBytes,
        long StderrBytes,
        long DurationMs,
        bool TimedOut);

    /// <summary>
    /// A capped string buffer that drops anything past the configured byte budget. Ensures
    /// a runaway process cannot exhaust the host process memory.
    /// </summary>
    private sealed class BoundedBuffer
    {
        private readonly int _max;
        private readonly StringBuilder _sb = new();
        private long _written;

        public BoundedBuffer(int max) => _max = max;

        public long WrittenBytes => _written;

        public void AppendLine(string line)
        {
            // Approximate: count UTF-8 bytes for the cap (worst case 4 bytes/char).
            // We use char count + line ending here as a proxy — fast and good enough.
            var add = line.Length + Environment.NewLine.Length;
            _written += add;
            if (_sb.Length >= _max) return;
            var room = _max - _sb.Length;
            if (line.Length + 1 <= room)
            {
                _sb.AppendLine(line);
            }
            else if (room > 0)
            {
                _sb.Append(line.AsSpan(0, Math.Min(line.Length, room)));
            }
        }

        public override string ToString() => _sb.ToString();
    }
}
