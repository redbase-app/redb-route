using redb.Route.Abstractions;
using redb.Route.GenericFile;

namespace redb.Route.File;

/// <summary>
/// File producer. Inherits the complete write flow from <see cref="GenericFileProducer{TOptions}"/>
/// (temp file, atomic rename, file-exist strategies).
/// Adds content-type inference for the produced file.
/// </summary>
public class FileProducer : GenericFileProducer<FileEndpointOptions>
{
    private readonly FileEndpoint _endpoint;

    /// <inheritdoc />
    protected override string BasePath => _endpoint.DirectoryPath;

    /// <inheritdoc />
    protected override string FileNameProducedHeader => FileHeaders.FileNameProduced;

    /// <summary>Creates a file producer.</summary>
    public FileProducer(FileEndpoint endpoint, FileEndpointOptions options)
        : base(endpoint, options, new LocalFileOperations())
    {
        _endpoint = endpoint;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The file name usually comes from an incoming message, so it is not trusted: a relative
    /// "../" name or an absolute path would otherwise write outside the endpoint directory
    /// (<see cref="Path.Combine(string,string)"/> silently discards the base for absolute paths).
    /// Set <see cref="FileEndpointOptions.JailStartingDirectory"/> to false to allow it.
    /// </remarks>
    protected override void ValidatePath(string targetPath)
    {
        if (!Options.JailStartingDirectory)
            return;

        var fullTarget = Path.GetFullPath(targetPath);
        var root = Path.GetFullPath(BasePath);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!GenericFileUtils.IsWithinDirectory(root, fullTarget, Path.DirectorySeparatorChar, comparison))
        {
            throw new UnauthorizedAccessException(
                $"Producer path '{targetPath}' resolves to '{fullTarget}', outside the endpoint directory '{root}'. " +
                "Set jailStartingDirectory=false to allow writing outside the base directory.");
        }
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
            // Try to get filename from incoming exchange headers
            var headerFileName = exchange.In.Headers.TryGetValue(FileHeaders.FileName, out var hfn)
                ? hfn?.ToString()
                : null;
            fileName = headerFileName ?? $"redb-{Guid.NewGuid():N}";
        }

        return Operations.CombinePath(BasePath, fileName);
    }
}
