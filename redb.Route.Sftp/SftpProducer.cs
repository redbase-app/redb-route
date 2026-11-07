using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.GenericFile;

namespace redb.Route.Sftp;

/// <summary>
/// SFTP producer. Uploads exchange body to a remote file on the SFTP server.
/// Supports atomic upload via temp files (TempPrefix/TempFileName → rename),
/// multiple file-exist strategies, dynamic file names via expressions, chmod,
/// automatic directory creation, and jail-path validation.
/// </summary>
public sealed class SftpProducer : RemoteFileProducer<SftpEndpointOptions>
{
    private readonly SftpEndpoint _endpoint;
    private readonly SftpFileOperations _sftpOps;

    /// <inheritdoc />
    protected override string BasePath => _endpoint.RemotePath;

    /// <inheritdoc />
    protected override string FileNameProducedHeader => SftpHeaders.FileNameProduced;

    /// <summary>Creates an SFTP producer.</summary>
    internal SftpProducer(SftpEndpoint endpoint, SftpEndpointOptions options, SftpFileOperations operations)
        : base(endpoint, options, operations)
    {
        _endpoint = endpoint;
        _sftpOps = operations;
    }

    /// <inheritdoc />
    protected override string ResolveTargetPath(IExchange exchange)
    {
        string fileName;

        if (Options.FileName != null)
        {
            fileName = Options.FileName.Value.Resolve(exchange) ?? "output";
        }
        else
        {
            // Try to get filename from incoming exchange headers (SFTP-specific first)
            var headerFileName = exchange.In.Headers.TryGetValue(SftpHeaders.FileName, out var hfn)
                ? hfn?.ToString()
                : null;

            // Fallback to generic file header
            if (string.IsNullOrEmpty(headerFileName))
            {
                headerFileName = exchange.In.Headers.TryGetValue("redbFile.Name", out var ffn)
                    ? ffn?.ToString()
                    : null;
            }

            fileName = headerFileName ?? $"redb-{Guid.NewGuid():N}";
        }

        if (Options.Flatten)
        {
            var lastSlash = fileName.LastIndexOf('/');
            if (lastSlash >= 0)
                fileName = fileName[(lastSlash + 1)..];
        }

        return Operations.CombinePath(BasePath, fileName);
    }

    /// <inheritdoc />
    protected override void ValidatePath(string targetPath)
    {
        if (!Options.JailStartingDirectory)
            return;

        var normalizedTarget = NormalizePath(targetPath);
        if (!GenericFileUtils.IsWithinDirectory(BasePath, normalizedTarget, '/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Producer path '{targetPath}' escapes the jail directory '{BasePath}'. " +
                "Set JailStartingDirectory=false to allow writing outside the base directory.");
        }
    }

    /// <inheritdoc />
    protected override async Task BeforeWriteAsync(string targetDir, CancellationToken ct)
    {
        await base.BeforeWriteAsync(targetDir, ct).ConfigureAwait(false);

        // Apply directory chmod if configured
        if (Options.AutoCreate && !string.IsNullOrEmpty(Options.ChmodDirectory))
        {
            try
            {
                _sftpOps.ApplyChmod(targetDir, Options.ChmodDirectory);
            }
            catch (Exception ex)
            {
                Logger?.LogDebug(ex, "SFTP: chmod {Permissions} failed for directory {Path}",
                    Options.ChmodDirectory, targetDir);
            }
        }
    }

    /// <inheritdoc />
    protected override Task AfterWriteAsync(IExchange exchange, string targetPath, CancellationToken ct)
    {
        // Apply file chmod if configured
        if (!string.IsNullOrEmpty(Options.Chmod))
        {
            try
            {
                _sftpOps.ApplyChmod(targetPath, Options.Chmod);
            }
            catch (Exception ex)
            {
                Logger?.LogDebug(ex, "SFTP: chmod {Permissions} failed for {Path}",
                    Options.Chmod, targetPath);
            }
        }

        // Preserve last modified time if requested
        if (Options.KeepLastModified &&
            exchange.In.Headers.TryGetValue(SftpHeaders.FileLastModified, out var lastModObj) &&
            lastModObj is DateTimeOffset lastMod)
        {
            try
            {
                var attrs = _sftpOps.GetAttributes(targetPath);
                attrs.LastWriteTime = lastMod.UtcDateTime;
                _sftpOps.SetAttributes(targetPath, attrs);
            }
            catch (Exception ex)
            {
                Logger?.LogDebug(ex, "SFTP: failed to preserve LastModified for {Path}", targetPath);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task MoveExistingFile(string path, CancellationToken ct)
    {
        var suffix = Options.MoveExistingFileStrategy switch
        {
            SftpMoveExistingStrategy.Backup => ".bak",
            SftpMoveExistingStrategy.Timestamp => "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"),
            SftpMoveExistingStrategy.Guid => "." + Guid.NewGuid().ToString("N"),
            _ => "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff")
        };

        var backupPath = path + suffix;
        await Operations.MoveAsync(path, backupPath, false, ct).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string NormalizePath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var part in parts)
        {
            if (part == "..")
            {
                if (stack.Count > 0) stack.Pop();
            }
            else if (part != ".")
            {
                stack.Push(part);
            }
        }
        return "/" + string.Join("/", stack.Reverse());
    }
}
