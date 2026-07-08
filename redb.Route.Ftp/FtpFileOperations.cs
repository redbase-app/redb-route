using System.Text;
using FluentFTP;
using redb.Route.GenericFile;

namespace redb.Route.Ftp;

/// <summary>
/// Implements <see cref="IRemoteFileOperations"/> for FTP via FluentFTP.
/// Manages <see cref="AsyncFtpClient"/> lifecycle through Connect/Disconnect.
/// All path operations use forward-slash convention.
/// </summary>
internal sealed class FtpFileOperations : IRemoteFileOperations
{
    private readonly FtpEndpointOptions _options;
    private AsyncFtpClient? _client;

    /// <summary>Creates FTP file operations for the given endpoint options.</summary>
    public FtpFileOperations(FtpEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The underlying FluentFTP client (null until connected).</summary>
    internal AsyncFtpClient? Client => _client;

    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION LIFECYCLE
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public bool IsConnected => _client is { IsConnected: true };

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_client != null)
        {
            if (_client.IsConnected)
                await _client.Disconnect(ct).ConfigureAwait(false);
            _client.Dispose();
            _client = null;
        }

        _client = new AsyncFtpClient(_options.Host, _options.Username, _options.Password, _options.Port);

        _client.Config.DataConnectionType = _options.PassiveMode
            ? FtpDataConnectionType.AutoPassive
            : FtpDataConnectionType.AutoActive;

        _client.Config.ConnectTimeout = _options.ConnectionTimeout;
        _client.Config.DataConnectionConnectTimeout = _options.ConnectionTimeout;
        _client.Config.ReadTimeout = _options.OperationTimeout;

        _client.Config.TransferChunkSize = 65536;

        if (_options.UseFtps)
        {
            _client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            if (!_options.ValidateCertificate)
                _client.ValidateCertificate += (_, e) => e.Accept = true;
        }

        await _client.Connect(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client != null)
        {
            try
            {
                if (_client.IsConnected)
                    await _client.Disconnect(ct).ConfigureAwait(false);
            }
            finally
            {
                _client.Dispose();
                _client = null;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ENUMERATION
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<List<GenericFileInfo>> ListFilesAsync(
        string directory, bool recursive, int maxDepth, int minDepth,
        CancellationToken ct = default)
    {
        var client = RequireClient();
        var result = new List<GenericFileInfo>();
        await EnumerateFilesAsync(client, directory, directory, 0, recursive, maxDepth, minDepth, result, ct)
            .ConfigureAwait(false);
        return result;
    }

    private async Task EnumerateFilesAsync(
        AsyncFtpClient client, string dirPath, string basePath,
        int depth, bool recursive, int maxDepth, int minDepth,
        List<GenericFileInfo> result, CancellationToken ct)
    {
        FtpListItem[] items;
        try
        {
            items = await client.GetListing(dirPath, FtpListOption.Modify, ct).ConfigureAwait(false);
        }
        catch (Exception) when (_options.IgnoreFileNotFoundOrPermissionError)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.Type == FtpObjectType.Directory)
            {
                if (recursive && (maxDepth == 0 || depth < maxDepth))
                {
                    var subPath = CombinePath(dirPath, item.Name);
                    await EnumerateFilesAsync(client, subPath, basePath, depth + 1, recursive, maxDepth, minDepth, result, ct)
                        .ConfigureAwait(false);
                }
            }
            else if (item.Type == FtpObjectType.File)
            {
                if (minDepth > 0 && depth < minDepth)
                    continue;

                result.Add(new GenericFileInfo
                {
                    Name = item.Name,
                    FullPath = item.FullName,
                    BasePath = basePath,
                    Length = item.Size,
                    LastModified = new DateTimeOffset(item.Modified.ToUniversalTime(), TimeSpan.Zero),
                    Depth = depth
                });
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  READ
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        using var ms = new MemoryStream();
        await client.DownloadStream(ms, path, token: ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        var ms = new MemoryStream();
        await client.DownloadStream(ms, path, token: ct).ConfigureAwait(false);
        ms.Position = 0;
        return ms;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  WRITE
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task WriteAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var client = RequireClient();
        using var ms = new MemoryStream(data);
        var status = await client.UploadStream(ms, path, FtpRemoteExists.Overwrite, true, null, ct)
            .ConfigureAwait(false);
        if (status == FtpStatus.Failed)
            throw new IOException($"FTP upload failed: {path}");
    }

    /// <inheritdoc />
    public async Task WriteAsync(string path, Stream data, CancellationToken ct = default)
    {
        var client = RequireClient();
        using var ms = new MemoryStream();
        await data.CopyToAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;
        var status = await client.UploadStream(ms, path, FtpRemoteExists.Overwrite, true, null, ct)
            .ConfigureAwait(false);
        if (status == FtpStatus.Failed)
            throw new IOException($"FTP upload failed: {path}");
    }

    /// <inheritdoc />
    public async Task AppendAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var client = RequireClient();
        using var ms = new MemoryStream(data);
        var status = await client.UploadStream(ms, path, FtpRemoteExists.AddToEnd, true, null, ct)
            .ConfigureAwait(false);
        if (status == FtpStatus.Failed)
            throw new IOException($"FTP append failed: {path}");
    }

    /// <inheritdoc />
    public async Task AppendTextAsync(string path, string text, Encoding encoding, CancellationToken ct = default)
    {
        var data = encoding.GetBytes(text);
        await AppendAsync(path, data, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FILE OPERATIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        return await client.FileExists(path, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        await client.DeleteFile(path, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct = default)
    {
        var client = RequireClient();
        if (overwrite && await client.FileExists(destination, ct).ConfigureAwait(false))
        {
            await client.DeleteFile(destination, ct).ConfigureAwait(false);
        }
        await client.MoveFile(source, destination, FtpRemoteExists.Overwrite, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DIRECTORY OPERATIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        await client.CreateDirectory(path, true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        return await client.DirectoryExists(path, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PATH HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public string CombinePath(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(basePath)) return "/" + relativePath;
        if (string.IsNullOrEmpty(relativePath)) return basePath;
        return basePath.TrimEnd('/') + "/" + relativePath.TrimStart('/');
    }

    /// <inheritdoc />
    public string GetParentPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : path[..lastSlash];
    }

    /// <inheritdoc />
    public string GetFileName(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }

    /// <inheritdoc />
    public string GetFileNameWithoutExtension(string name)
    {
        var fileName = GetFileName(name);
        var dotIndex = fileName.LastIndexOf('.');
        return dotIndex > 0 ? fileName[..dotIndex] : fileName;
    }

    /// <inheritdoc />
    public string GetExtension(string name)
    {
        var fileName = GetFileName(name);
        var dotIndex = fileName.LastIndexOf('.');
        return dotIndex >= 0 ? fileName[dotIndex..] : "";
    }

    /// <inheritdoc />
    public string GetRelativePath(string basePath, string fullPath)
    {
        if (fullPath.StartsWith(basePath, StringComparison.Ordinal))
        {
            var relative = fullPath[basePath.Length..].TrimStart('/');
            return string.IsNullOrEmpty(relative) ? fullPath : relative;
        }
        return fullPath;
    }

    /// <inheritdoc />
    public bool IsAbsolutePath(string path)
    {
        return path.StartsWith('/');
    }

    // ═══════════════════════════════════════════════════════════════════
    //  INTERNAL
    // ═══════════════════════════════════════════════════════════════════

    private AsyncFtpClient RequireClient()
    {
        return _client ?? throw new InvalidOperationException("FTP client is not connected. Call ConnectAsync first.");
    }
}
