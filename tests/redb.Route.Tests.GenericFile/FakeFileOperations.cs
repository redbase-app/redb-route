using System.Text;
using redb.Route.GenericFile;

namespace redb.Route.Tests.GenericFile;

/// <summary>
/// In-memory <see cref="IFileOperations"/> for testing the shared consumer/producer
/// pipeline of <c>redb.Route.GenericFile</c> without a real file system or server.
///
/// Paths are POSIX-style ("/in/order.csv"). Directories are implicit: a directory
/// exists when it was explicitly created or when it is a prefix of an existing file.
///
/// Fault injection: assign <see cref="FailRead"/>, <see cref="FailDelete"/> or
/// <see cref="FailMove"/> to make the corresponding operation throw for a given path.
/// </summary>
public sealed class FakeFileOperations : IFileOperations
{
    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal) { "/" };

    private sealed class Entry
    {
        public required byte[] Data { get; set; }
        public required DateTimeOffset LastModified { get; set; }
    }

    /// <summary>Return a non-null exception to make <c>ReadAllBytes</c>/<c>OpenRead</c> fail for that path.</summary>
    public Func<string, Exception?>? FailRead { get; set; }

    /// <summary>Return a non-null exception to make <c>Delete</c> fail for that path.</summary>
    public Func<string, Exception?>? FailDelete { get; set; }

    /// <summary>Return a non-null exception to make <c>Move</c> fail for that source path.</summary>
    public Func<string, Exception?>? FailMove { get; set; }

    // ── Test helpers ────────────────────────────────────────────────

    /// <summary>Adds a file, creating its parent directories.</summary>
    public void AddFile(string path, string content, DateTimeOffset? lastModified = null)
        => AddFile(path, Encoding.UTF8.GetBytes(content), lastModified);

    /// <summary>Adds a file, creating its parent directories.</summary>
    public void AddFile(string path, byte[] content, DateTimeOffset? lastModified = null)
    {
        EnsureDir(GetParentPath(path));
        _files[path] = new Entry
        {
            Data = content,
            LastModified = lastModified ?? DateTimeOffset.UtcNow
        };
    }

    /// <summary>Adds a directory.</summary>
    public void AddDirectory(string path) => EnsureDir(path);

    /// <summary>True when a file exists at the path.</summary>
    public bool HasFile(string path) => _files.ContainsKey(path);

    /// <summary>Content of an existing file as UTF-8 text.</summary>
    public string TextAt(string path) => Encoding.UTF8.GetString(_files[path].Data);

    /// <summary>All file paths currently present, sorted.</summary>
    public IReadOnlyList<string> AllFiles() => _files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>All directory paths currently present, sorted.</summary>
    public IReadOnlyList<string> AllDirectories() => _dirs.OrderBy(k => k, StringComparer.Ordinal).ToList();

    private void EnsureDir(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        _dirs.Add("/");
        foreach (var p in parts)
        {
            current += "/" + p;
            _dirs.Add(current);
        }
    }

    // ── Enumeration ─────────────────────────────────────────────────

    public Task<List<GenericFileInfo>> ListFilesAsync(
        string directory, bool recursive, int maxDepth, int minDepth, CancellationToken ct = default)
    {
        var basePath = directory.TrimEnd('/');
        if (basePath.Length == 0) basePath = "/";
        var prefix = basePath == "/" ? "/" : basePath + "/";

        var result = new List<GenericFileInfo>();
        foreach (var (path, entry) in _files)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rel = path[prefix.Length..];
            var depth = rel.Count(c => c == '/');

            if (!recursive && depth > 0) continue;
            if (maxDepth > 0 && depth > maxDepth) continue;
            if (minDepth > 0 && depth < minDepth) continue;

            result.Add(new GenericFileInfo
            {
                Name = GetFileName(path),
                FullPath = path,
                BasePath = basePath,
                Length = entry.Data.Length,
                LastModified = entry.LastModified,
                Depth = depth
            });
        }

        return Task.FromResult(result);
    }

    // ── Read ────────────────────────────────────────────────────────

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        var injected = FailRead?.Invoke(path);
        if (injected != null) throw injected;

        if (!_files.TryGetValue(path, out var e))
            throw new FileNotFoundException($"No such file: {path}", path);

        return Task.FromResult(e.Data.ToArray());
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var injected = FailRead?.Invoke(path);
        if (injected != null) throw injected;

        if (!_files.TryGetValue(path, out var e))
            throw new FileNotFoundException($"No such file: {path}", path);

        return Task.FromResult<Stream>(new MemoryStream(e.Data.ToArray(), writable: false));
    }

    // ── Write ───────────────────────────────────────────────────────

    public Task WriteAsync(string path, byte[] data, CancellationToken ct = default)
    {
        AddFile(path, data.ToArray());
        return Task.CompletedTask;
    }

    public async Task WriteAsync(string path, Stream data, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await data.CopyToAsync(ms, ct).ConfigureAwait(false);
        AddFile(path, ms.ToArray());
    }

    public Task AppendAsync(string path, byte[] data, CancellationToken ct = default)
    {
        if (_files.TryGetValue(path, out var e))
            e.Data = [.. e.Data, .. data];
        else
            AddFile(path, data.ToArray());
        return Task.CompletedTask;
    }

    public Task AppendTextAsync(string path, string text, Encoding encoding, CancellationToken ct = default)
        => AppendAsync(path, encoding.GetBytes(text), ct);

    // ── File operations ─────────────────────────────────────────────

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_files.ContainsKey(path));

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var injected = FailDelete?.Invoke(path);
        if (injected != null) throw injected;

        _files.Remove(path);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct = default)
    {
        var injected = FailMove?.Invoke(source);
        if (injected != null) throw injected;

        if (!_files.TryGetValue(source, out var e))
            throw new FileNotFoundException($"No such file: {source}", source);

        if (_files.ContainsKey(destination) && !overwrite)
            throw new IOException($"Destination already exists: {destination}");

        _files.Remove(source);
        EnsureDir(GetParentPath(destination));
        _files[destination] = e;
        return Task.CompletedTask;
    }

    // ── Directory operations ────────────────────────────────────────

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        EnsureDir(path);
        return Task.CompletedTask;
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        var normalized = path.TrimEnd('/');
        if (normalized.Length == 0) normalized = "/";
        return Task.FromResult(_dirs.Contains(normalized));
    }

    // ── Path helpers ────────────────────────────────────────────────

    public string CombinePath(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(basePath)) return "/" + relativePath.TrimStart('/');
        if (string.IsNullOrEmpty(relativePath)) return basePath;
        return basePath.TrimEnd('/') + "/" + relativePath.TrimStart('/');
    }

    public string GetParentPath(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    public string GetFileName(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    public string GetFileNameWithoutExtension(string name)
    {
        var file = GetFileName(name);
        var dot = file.LastIndexOf('.');
        return dot <= 0 ? file : file[..dot];
    }

    public string GetExtension(string name)
    {
        var file = GetFileName(name);
        var dot = file.LastIndexOf('.');
        return dot <= 0 ? "" : file[dot..];
    }

    public string GetRelativePath(string basePath, string fullPath)
    {
        var prefix = basePath.TrimEnd('/') + "/";
        return fullPath.StartsWith(prefix, StringComparison.Ordinal)
            ? fullPath[prefix.Length..]
            : fullPath;
    }

    public bool IsAbsolutePath(string path) => path.StartsWith('/');
}
