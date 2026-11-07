using redb.Route.Abstractions;
using redb.Route.GenericFile;

namespace redb.Route.Ftp;

/// <summary>
/// FTP consumer. Polls a remote directory at regular intervals via FluentFTP,
/// applies glob filters, idempotency, pre/post-move, and processes each file via the route processor.
/// After successful processing, applies the configured post-processing strategy (noop/delete/move).
/// Supports recursive directory scanning, min/max age filtering, done-file markers,
/// and automatic reconnection.
/// </summary>
public sealed class FtpConsumer : RemoteFileConsumer<FtpEndpointOptions>
{
    private readonly FtpEndpoint _endpoint;

    /// <inheritdoc />
    protected override string BasePath => _endpoint.RemotePath;

    /// <inheritdoc />
    protected override string ConsumerName => $"ftp:{Options.Host}:{Options.Port}{_endpoint.Uri.Path}";

    /// <summary>Creates an FTP consumer.</summary>
    internal FtpConsumer(FtpEndpoint endpoint, IProcessor processor, FtpEndpointOptions options, FtpFileOperations operations)
        : base(endpoint, processor, options, operations)
    {
        _endpoint = endpoint;
    }

    /// <inheritdoc />
    protected override void SetExchangeHeaders(IMessage message, GenericFileInfo file, string workPath)
    {
        var relativePath = Operations.GetRelativePath(file.BasePath, workPath);
        var parentDir = Operations.GetParentPath(workPath);

        message.Headers[FtpHeaders.FileName] = file.Name;
        message.Headers[FtpHeaders.FileNameOnly] = Operations.GetFileNameWithoutExtension(file.Name);
        message.Headers[FtpHeaders.FileExtension] = Operations.GetExtension(file.Name);
        message.Headers[FtpHeaders.RemotePath] = workPath;
        message.Headers[FtpHeaders.RelativePath] = relativePath;
        message.Headers[FtpHeaders.RemoteParent] = parentDir;
        message.Headers[FtpHeaders.FileLength] = file.Length;
        message.Headers[FtpHeaders.FileLastModified] = file.LastModified;
        message.Headers[FtpHeaders.Host] = Options.Host;
        message.Headers[FtpHeaders.Port] = Options.Port;
        message.Headers[FtpHeaders.Username] = Options.Username;
    }
}
