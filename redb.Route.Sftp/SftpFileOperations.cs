using System.Text;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using redb.Route.GenericFile;

namespace redb.Route.Sftp;

/// <summary>
/// Implements <see cref="IRemoteFileOperations"/> for SFTP via SSH.NET.
/// Manages <see cref="SftpClient"/> lifecycle through Connect/Disconnect.
/// All path operations use forward-slash UNIX convention.
/// </summary>
internal sealed class SftpFileOperations : IRemoteFileOperations
{
    private readonly SftpEndpointOptions _options;
    private SftpClient? _client;

    /// <summary>Creates SFTP file operations for the given endpoint options.</summary>
    public SftpFileOperations(SftpEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The underlying SSH.NET SFTP client (null until connected).</summary>
    internal SftpClient? Client => _client;

    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION LIFECYCLE
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public bool IsConnected => _client is { IsConnected: true };

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (_client != null)
        {
            if (_client.IsConnected) _client.Disconnect();
            _client.Dispose();
            _client = null;
        }

        _client = SftpClientFactory.Create(_options);
        _client.Connect();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client != null)
        {
            try
            {
                if (_client.IsConnected)
                    _client.Disconnect();
            }
            finally
            {
                _client.Dispose();
                _client = null;
            }
        }

        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ENUMERATION
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<List<GenericFileInfo>> ListFilesAsync(
        string directory, bool recursive, int maxDepth, int minDepth,
        CancellationToken ct = default)
    {
        var client = RequireClient();
        var result = EnumerateFiles(client, directory, directory, 0, recursive, maxDepth, minDepth);
        return Task.FromResult(result);
    }

    private List<GenericFileInfo> EnumerateFiles(
        SftpClient client, string dirPath, string basePath,
        int depth, bool recursive, int maxDepth, int minDepth)
    {
        var result = new List<GenericFileInfo>();

        IEnumerable<ISftpFile> entries;
        try
        {
            entries = client.ListDirectory(dirPath);
        }
        catch (Exception) when (_options.IgnoreFileNotFoundOrPermissionError)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..")
                continue;

            if (entry.IsDirectory)
            {
                if (recursive && (maxDepth == 0 || depth < maxDepth))
                {
                    var subPath = CombinePath(dirPath, entry.Name);
                    result.AddRange(EnumerateFiles(client, subPath, basePath, depth + 1, recursive, maxDepth, minDepth));
                }
            }
            else if (entry.IsRegularFile)
            {
                if (minDepth > 0 && depth < minDepth)
                    continue;

                result.Add(new GenericFileInfo
                {
                    Name = entry.Name,
                    FullPath = entry.FullName,
                    BasePath = basePath,
                    Length = entry.Length,
                    LastModified = new DateTimeOffset(entry.LastWriteTime.ToUniversalTime(), TimeSpan.Zero),
                    Depth = depth,
                    Extras = new Dictionary<string, object>
                    {
                        ["UserId"] = entry.UserId,
                        ["GroupId"] = entry.GroupId
                    }
                });
            }
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  READ
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        using var ms = new MemoryStream();
        client.DownloadFile(path, ms);
        return Task.FromResult(ms.ToArray());
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        Stream stream = client.OpenRead(path);
        return Task.FromResult(stream);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  WRITE
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task WriteAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var client = RequireClient();
        using var ms = new MemoryStream(data);
        client.UploadFile(ms, path);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteAsync(string path, Stream data, CancellationToken ct = default)
    {
        var client = RequireClient();
        // Copy to MemoryStream first to ensure seekability
        using var ms = new MemoryStream();
        await data.CopyToAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;
        client.UploadFile(ms, path);
    }

    /// <inheritdoc />
    public Task AppendAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var client = RequireClient();
        var encoding = ResolveEncoding();
        using var writer = client.AppendText(path, encoding);
        writer.Write(encoding.GetString(data));
        writer.Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AppendTextAsync(string path, string text, Encoding encoding, CancellationToken ct = default)
    {
        var client = RequireClient();
        using var writer = client.AppendText(path, encoding);
        writer.Write(text);
        writer.Flush();
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FILE OPERATIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        return Task.FromResult(client.Exists(path));
    }

    /// <inheritdoc />
    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        client.DeleteFile(path);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct = default)
    {
        var client = RequireClient();
        if (overwrite && client.Exists(destination))
        {
            client.DeleteFile(destination);
        }
        client.RenameFile(source, destination);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DIRECTORY OPERATIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        EnsureRemoteDirectoryExists(client, path);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        var client = RequireClient();
        return Task.FromResult(client.Exists(path));
    }

    /// <summary>
    /// Recursively creates all directories in the given path.
    /// </summary>
    internal static void EnsureRemoteDirectoryExists(SftpClient client, string path)
    {
        if (client.Exists(path))
            return;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
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
    //  SFTP-SPECIFIC HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies POSIX chmod to a file or directory.
    /// </summary>
    internal void ApplyChmod(string path, string octalPermissions)
    {
        var client = RequireClient();
        var mode = SftpEndpointOptions.ParseOctalPermissions(octalPermissions);
        var attrs = client.GetAttributes(path);
        attrs.SetPermissions(mode);
        client.SetAttributes(path, attrs);
    }

    /// <summary>
    /// Gets file attributes from the remote server.
    /// </summary>
    internal SftpFileAttributes GetAttributes(string path)
    {
        var client = RequireClient();
        return client.GetAttributes(path);
    }

    /// <summary>
    /// Sets file attributes on the remote server.
    /// </summary>
    internal void SetAttributes(string path, SftpFileAttributes attrs)
    {
        var client = RequireClient();
        client.SetAttributes(path, attrs);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  INTERNALS
    // ═══════════════════════════════════════════════════════════════════

    private SftpClient RequireClient()
    {
        return _client ?? throw new InvalidOperationException(
            "SFTP client is not connected. Call ConnectAsync() first.");
    }

    private Encoding ResolveEncoding()
    {
        try
        {
            return Encoding.GetEncoding(_options.Charset);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
