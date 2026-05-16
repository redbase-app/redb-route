using redb.Route.Abstractions;
using redb.Route.GenericFile;

namespace redb.Route.Sftp;

/// <summary>
/// SFTP consumer. Polls a remote directory at regular intervals via SSH.NET,
/// applies glob filters, idempotency, pre/post-move, and processes each file via the route processor.
/// After successful processing, applies the configured post-processing strategy (noop/delete/move).
/// Supports recursive directory scanning, min/max age filtering, done-file markers,
/// and automatic reconnection.
/// </summary>
public sealed class SftpConsumer : RemoteFileConsumer<SftpEndpointOptions>
{
    private readonly SftpEndpoint _endpoint;

    /// <inheritdoc />
    protected override string BasePath => _endpoint.RemotePath;

    /// <inheritdoc />
    protected override string ConsumerName => $"sftp:{Options.Host}:{Options.Port}{_endpoint.Uri.Path}";

    /// <summary>Creates an SFTP consumer.</summary>
    internal SftpConsumer(SftpEndpoint endpoint, IProcessor processor, SftpEndpointOptions options, SftpFileOperations operations)
        : base(endpoint, processor, options, operations)
    {
        _endpoint = endpoint;
    }

    /// <inheritdoc />
    protected override void SetExchangeHeaders(IMessage message, GenericFileInfo file, string workPath)
    {
        var relativePath = Operations.GetRelativePath(file.BasePath, workPath);
        var parentDir = Operations.GetParentPath(workPath);

        message.Headers[SftpHeaders.FileName] = file.Name;
        message.Headers[SftpHeaders.FileNameOnly] = Operations.GetFileNameWithoutExtension(file.Name);
        message.Headers[SftpHeaders.FileExtension] = Operations.GetExtension(file.Name);
        message.Headers[SftpHeaders.RemotePath] = workPath;
        message.Headers[SftpHeaders.RelativePath] = relativePath;
        message.Headers[SftpHeaders.RemoteParent] = parentDir;
        message.Headers[SftpHeaders.FileLength] = file.Length;
        message.Headers[SftpHeaders.FileLastModified] = file.LastModified;
        message.Headers[SftpHeaders.Host] = Options.Host;
        message.Headers[SftpHeaders.Port] = Options.Port;
        message.Headers[SftpHeaders.Username] = Options.Username;

        // SFTP-specific: POSIX owner/group from Extras
        if (file.Extras != null)
        {
            if (file.Extras.TryGetValue("UserId", out var uid))
                message.Headers[SftpHeaders.FileOwner] = uid;
            if (file.Extras.TryGetValue("GroupId", out var gid))
                message.Headers[SftpHeaders.FileGroup] = gid;
        }
    }
}
