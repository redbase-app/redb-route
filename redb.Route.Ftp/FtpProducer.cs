using redb.Route.Abstractions;
using redb.Route.GenericFile;

namespace redb.Route.Ftp;

/// <summary>
/// FTP producer. Uploads exchange body to a remote file on the FTP server.
/// Supports atomic upload via temp files (TempPrefix/TempFileName → rename),
/// multiple file-exist strategies, dynamic file names via expressions,
/// automatic directory creation, and jail-path validation.
/// </summary>
public sealed class FtpProducer : RemoteFileProducer<FtpEndpointOptions>
{
    private readonly FtpEndpoint _endpoint;

    /// <inheritdoc />
    protected override string BasePath => _endpoint.RemotePath;

    /// <inheritdoc />
    protected override string FileNameProducedHeader => FtpHeaders.FileNameProduced;

    /// <summary>Creates an FTP producer.</summary>
    internal FtpProducer(FtpEndpoint endpoint, FtpEndpointOptions options, FtpFileOperations operations)
        : base(endpoint, options, operations)
    {
        _endpoint = endpoint;
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
            // Try to get filename from incoming exchange headers (FTP-specific first)
            var headerFileName = exchange.In.Headers.TryGetValue(FtpHeaders.FileName, out var hfn)
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
    protected override async Task MoveExistingFile(string path, CancellationToken ct)
    {
        var suffix = Options.MoveExistingFileStrategy switch
        {
            FtpMoveExistingStrategy.Backup => ".bak",
            FtpMoveExistingStrategy.Timestamp => "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"),
            FtpMoveExistingStrategy.Guid => "." + Guid.NewGuid().ToString("N"),
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
