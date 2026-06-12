using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Llm.Mcp.Transport;

/// <summary>
/// Stdio transport — spawns an external process, exchanges newline-delimited
/// UTF-8 JSON-RPC frames over stdin/stdout, drains stderr to the logger.
/// All stdin writes are serialised through a <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class StdioMcpClient : McpClientBase
{
    private readonly McpTransport _transport;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // BOM-less UTF-8: many MCP servers (Serena, Anthropic reference) reject a
    // BOM-prefixed first frame as invalid JSON. The static Encoding.UTF8 emits
    // a BOM on WriteLine of the first chunk, so we use UTF8Encoding(false).
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private Process? _process;
    private CancellationTokenSource? _readCts;
    private Task? _stdoutPump;
    private Task? _stderrPump;

    /// <summary>Creates a new stdio MCP client.</summary>
    /// <param name="serverName">Logical server name.</param>
    /// <param name="transport">Stdio transport configuration.</param>
    /// <param name="logger">Logger for trace/info/warning/error messages.</param>
    public StdioMcpClient(string serverName, McpTransport transport, ILogger logger)
        : base(serverName, logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (transport.Kind != McpTransportKind.Stdio)
            throw new ArgumentException("StdioMcpClient requires a stdio transport.", nameof(transport));
        if (string.IsNullOrWhiteSpace(transport.Command))
            throw new ArgumentException("Stdio transport requires a Command.", nameof(transport));
        _transport = transport;
    }

    /// <inheritdoc />
    protected override Task StartTransportAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _transport.Command!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        foreach (var arg in _transport.Arguments)
            psi.ArgumentList.Add(arg);

        if (!string.IsNullOrEmpty(_transport.WorkingDirectory))
            psi.WorkingDirectory = _transport.WorkingDirectory;

        // Start from caller's environment, then overlay extras.
        foreach (var (k, v) in _transport.Environment)
            psi.EnvironmentVariables[k] = v;

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Exited += OnProcessExited;

        try
        {
            if (!proc.Start())
                throw new McpException($"MCP stdio server '{ServerName}' failed to start (Process.Start returned false).");
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(
                $"MCP stdio server '{ServerName}' failed to start: {_transport.Command} ({ex.Message})", ex);
        }

        Logger.LogInformation(
            "MCP stdio server '{Server}' spawned (pid {Pid}): {Command} {Args}",
            ServerName, proc.Id, _transport.Command, string.Join(' ', _transport.Arguments));

        _process = proc;
        _readCts = new CancellationTokenSource();
        _stdoutPump = Task.Run(() => StdoutPumpAsync(proc, _readCts.Token));
        _stderrPump = Task.Run(() => StderrPumpAsync(proc, _readCts.Token));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task StopTransportAsync()
    {
        try { _readCts?.Cancel(); } catch { /* ignored */ }

        var proc = _process;
        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2_000);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "MCP {Server} process kill threw.", ServerName);
            }
            finally
            {
                proc.Exited -= OnProcessExited;
                proc.Dispose();
                _process = null;
            }
        }

        try
        {
            if (_stdoutPump is not null) await _stdoutPump.ConfigureAwait(false);
            if (_stderrPump is not null) await _stderrPump.ConfigureAwait(false);
        }
        catch { /* pumps own exceptions go through OnTransportFailed */ }

        _stdoutPump = null;
        _stderrPump = null;
        _readCts?.Dispose();
        _readCts = null;
    }

    /// <inheritdoc />
    protected override async Task SendFrameAsync(string frameJson, CancellationToken cancellationToken)
    {
        var proc = _process;
        if (proc is null || proc.HasExited)
            throw new McpException($"MCP stdio server '{ServerName}' is not running.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var writer = proc.StandardInput;
            await writer.WriteAsync(frameJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync('\n').ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task StdoutPumpAsync(Process proc, CancellationToken ct)
    {
        try
        {
            var reader = proc.StandardOutput;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                // Skip non-JSON log lines that might bleed into stdout.
                var trimmed = line.AsSpan().TrimStart();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                {
                    Logger.LogTrace("MCP {Server} stdout (non-frame): {Line}", ServerName, line);
                    continue;
                }

                OnFrameReceived(line);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "MCP {Server} stdout pump terminated.", ServerName);
            OnTransportFailed($"stdout pump error: {ex.Message}");
        }
    }

    private async Task StderrPumpAsync(Process proc, CancellationToken ct)
    {
        try
        {
            var reader = proc.StandardError;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                Logger.LogTrace("MCP {Server} stderr: {Line}", ServerName, line);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "MCP {Server} stderr pump terminated.", ServerName);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (Status is McpClientStatus.Dead) return;
        var code = (sender as Process)?.ExitCode ?? -1;
        Logger.LogWarning("MCP {Server} process exited unexpectedly (code {Code}).", ServerName, code);
        OnTransportFailed($"process exited with code {code}");
    }
}
