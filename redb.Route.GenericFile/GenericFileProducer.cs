using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.GenericFile;

/// <summary>
/// Template-method base class for all file-based producers (local file, SFTP, FTP).
/// Owns the complete write flow: resolve body → resolve path → handle existing file →
/// write via temp → atomic rename → cleanup on failure.
/// Subclasses provide protocol-specific hooks (connection, chmod, jail check).
/// </summary>
/// <typeparam name="TOptions">Concrete options type inheriting <see cref="GenericFileEndpointOptions"/>.</typeparam>
public abstract class GenericFileProducer<TOptions> : IProducer
    where TOptions : GenericFileEndpointOptions
{
    private ILogger? _logger;

    /// <summary>Endpoint this producer belongs to.</summary>
    protected IEndpoint ProducerEndpoint { get; }

    /// <summary>Endpoint options.</summary>
    protected TOptions Options { get; }

    /// <summary>File operations (protocol-specific adapter).</summary>
    protected IFileOperations Operations { get; }

    /// <summary>Logger resolved from the component.</summary>
    protected ILogger? Logger => _logger ??= (ProducerEndpoint.Component as ComponentBase)?.Logger;

    /// <summary>Base directory path for this producer.</summary>
    protected abstract string BasePath { get; }

    /// <summary>Header name for the produced file path (e.g. "redbFile.NameProduced").</summary>
    protected abstract string FileNameProducedHeader { get; }

    /// <summary>Creates a generic file producer.</summary>
    protected GenericFileProducer(IEndpoint endpoint, TOptions options, IFileOperations operations)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operations);
        ProducerEndpoint = endpoint;
        Options = options;
        Operations = operations;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PROCESS (template method)
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var body = ResolveBody(exchange);
        var targetPath = ResolveTargetPath(exchange);
        var targetDir = Operations.GetParentPath(targetPath);

        // Validate path (virtual — jail check for remote)
        ValidatePath(targetPath);

        // Before-write hook (connect if remote, auto-create directory)
        await BeforeWriteAsync(targetDir, ct).ConfigureAwait(false);

        // Determine the path to write to (may be temp file)
        var writePath = ResolveTempPath(targetPath);
        var isTemp = !string.Equals(writePath, targetPath, StringComparison.Ordinal);

        try
        {
            // Handle existing file
            if (await Operations.ExistsAsync(targetPath, ct).ConfigureAwait(false))
            {
                switch (Options.FileExist)
                {
                    case GenericFileExistStrategy.Fail:
                        throw new IOException(
                            $"File already exists and FileExist=Fail: {targetPath}");

                    case GenericFileExistStrategy.Ignore:
                        exchange.In.Headers[FileNameProducedHeader] = targetPath;
                        await OnWriteCompletedAsync(exchange, targetPath, ct).ConfigureAwait(false);
                        return;

                    case GenericFileExistStrategy.Append:
                        await AppendToFile(targetPath, body, ct).ConfigureAwait(false);
                        exchange.In.Headers[FileNameProducedHeader] = targetPath;
                        await OnWriteCompletedAsync(exchange, targetPath, ct).ConfigureAwait(false);
                        return;

                    case GenericFileExistStrategy.Move:
                        await MoveExistingFile(targetPath, ct).ConfigureAwait(false);
                        break;

                    case GenericFileExistStrategy.TryRename:
                        var renamePath = targetPath + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
                        try
                        {
                            await Operations.MoveAsync(targetPath, renamePath, false, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogWarning(ex, "TryRename failed for {Path}", targetPath);
                        }
                        break;

                    case GenericFileExistStrategy.Override:
                    default:
                        if (Options.EagerDeleteTargetFile)
                        {
                            try
                            {
                                await Operations.DeleteAsync(targetPath, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Logger?.LogWarning(ex, "EagerDelete failed for {Path}", targetPath);
                            }
                        }
                        break;
                }
            }

            // Write the file
            await WriteBody(writePath, body, ct).ConfigureAwait(false);

            // Atomic rename from temp to target
            if (isTemp)
            {
                // Delete target if it appeared during write
                if (await Operations.ExistsAsync(targetPath, ct).ConfigureAwait(false))
                {
                    try
                    {
                        await Operations.DeleteAsync(targetPath, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning(ex, "Failed to delete target before rename {Path}", targetPath);
                    }
                }

                await Operations.MoveAsync(writePath, targetPath, true, ct).ConfigureAwait(false);
            }

            exchange.In.Headers[FileNameProducedHeader] = targetPath;

            // After-write hook (chmod, keepLastModified, etc.)
            await AfterWriteAsync(exchange, targetPath, ct).ConfigureAwait(false);

            // Completion hook (disconnect if configured, etc.)
            await OnWriteCompletedAsync(exchange, targetPath, ct).ConfigureAwait(false);
        }
        catch
        {
            // Clean up temp file on failure
            if (isTemp)
            {
                try
                {
                    if (await Operations.ExistsAsync(writePath, ct).ConfigureAwait(false))
                        await Operations.DeleteAsync(writePath, ct).ConfigureAwait(false);
                }
                catch (Exception cleanupEx)
                {
                    Logger?.LogDebug(cleanupEx, "Failed to clean up temp file {Path}", writePath);
                }
            }
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public virtual Task Start(CancellationToken ct = default)
    {
        _logger ??= (ProducerEndpoint.Component as ComponentBase)?.Logger;
        Logger?.LogInformation("{Producer} started", ProducerEndpoint.Uri.NormalizedKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task Stop(CancellationToken ct = default)
    {
        Logger?.LogInformation("{Producer} stopped", ProducerEndpoint.Uri.NormalizedKey);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SHARED LOGIC
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Extracts the body from the exchange (Out preferred, falls back to In).</summary>
    protected object? ResolveBody(IExchange exchange)
    {
        var body = exchange.HasOut ? exchange.Out!.Body : exchange.In.Body;

        if (body == null && !Options.AllowNullBody)
        {
            throw new InvalidOperationException(
                "Exchange body is null and AllowNullBody=false. Set AllowNullBody=true to write empty files.");
        }

        return body;
    }

    /// <summary>Resolves the target file path from the FileName option or exchange headers.</summary>
    protected virtual string ResolveTargetPath(IExchange exchange)
    {
        string fileName;

        if (Options.FileName != null)
        {
            fileName = Options.FileName.Value.Resolve(exchange) ?? "output";
        }
        else
        {
            // Try to get filename from incoming exchange headers
            var headerFileName = exchange.In.Headers.TryGetValue(FileNameProducedHeader.Replace("Produced", ""), out var hfn)
                ? hfn?.ToString()
                : null;
            fileName = headerFileName ?? $"redb-{Guid.NewGuid():N}";
        }

        return Operations.CombinePath(BasePath, fileName);
    }

    /// <summary>Computes the temp file path if TempPrefix or TempFileName is configured.</summary>
    protected string ResolveTempPath(string targetPath)
    {
        if (!string.IsNullOrEmpty(Options.TempFileName))
        {
            var dir = Operations.GetParentPath(targetPath);
            return Operations.CombinePath(dir, Options.TempFileName);
        }

        if (!string.IsNullOrEmpty(Options.TempPrefix))
        {
            var dir = Operations.GetParentPath(targetPath);
            var name = Operations.GetFileName(targetPath);
            return Operations.CombinePath(dir, Options.TempPrefix + name);
        }

        return targetPath;
    }

    /// <summary>Writes the body to the given path.</summary>
    protected async Task WriteBody(string path, object? body, CancellationToken ct)
    {
        if (body == null)
        {
            await Operations.WriteAsync(path, Array.Empty<byte>(), ct).ConfigureAwait(false);
        }
        else if (body is byte[] bytes)
        {
            await Operations.WriteAsync(path, bytes, ct).ConfigureAwait(false);
        }
        else if (body is Stream stream)
        {
            await Operations.WriteAsync(path, stream, ct).ConfigureAwait(false);
        }
        else
        {
            var text = body.ToString() ?? "";
            var encoding = ResolveEncoding();
            var textBytes = encoding.GetBytes(text);
            await Operations.WriteAsync(path, textBytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Appends the body to the given path.</summary>
    protected async Task AppendToFile(string path, object? body, CancellationToken ct)
    {
        var encoding = ResolveEncoding();

        if (body is byte[] bytes)
        {
            await Operations.AppendAsync(path, bytes, ct).ConfigureAwait(false);
        }
        else if (body is Stream stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            await Operations.AppendAsync(path, ms.ToArray(), ct).ConfigureAwait(false);
        }
        else
        {
            var text = body?.ToString() ?? "";
            await Operations.AppendTextAsync(path, text, encoding, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(Options.AppendChars))
        {
            await Operations.AppendTextAsync(path, Options.AppendChars, encoding, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Moves an existing file at the target path to a backup name.</summary>
    protected virtual async Task MoveExistingFile(string path, CancellationToken ct)
    {
        var backupPath = path + ".bak";
        await Operations.MoveAsync(path, backupPath, true, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves encoding from the Charset option with UTF-8 fallback.</summary>
    protected Encoding ResolveEncoding()
    {
        try
        {
            return Encoding.GetEncoding(Options.Charset);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  VIRTUAL HOOKS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates the target path before writing. Override for jail-directory checks.
    /// </summary>
    protected virtual void ValidatePath(string targetPath) { }

    /// <summary>
    /// Called before writing. Override to connect to remote server, auto-create directory with chmod, etc.
    /// Default implementation auto-creates the target directory if configured.
    /// </summary>
    protected virtual async Task BeforeWriteAsync(string targetDir, CancellationToken ct)
    {
        if (Options.AutoCreate)
        {
            await Operations.CreateDirectoryAsync(targetDir, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called after a successful write. Override for chmod, keepLastModified, content-type inference.
    /// </summary>
    protected virtual Task AfterWriteAsync(IExchange exchange, string targetPath, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Called after write or early return (Ignore, Append). Override to disconnect if configured.
    /// </summary>
    protected virtual Task OnWriteCompletedAsync(IExchange exchange, string targetPath, CancellationToken ct)
        => Task.CompletedTask;
}
