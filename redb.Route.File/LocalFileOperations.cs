using System.Text;
using redb.Route.GenericFile;

namespace redb.Route.File;

/// <summary>
/// Local file system implementation of <see cref="IFileOperations"/>.
/// Adapts System.IO APIs to the generic file operations interface.
/// </summary>
internal sealed class LocalFileOperations : IFileOperations
{
    public Task<List<GenericFileInfo>> ListFilesAsync(
        string directory, bool recursive, int maxDepth, int minDepth, CancellationToken ct)
    {
        var dir = new DirectoryInfo(directory);
        if (!dir.Exists)
            return Task.FromResult(new List<GenericFileInfo>());

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var result = new List<GenericFileInfo>();

        try
        {
            foreach (var file in dir.EnumerateFiles("*", searchOption))
            {
                ct.ThrowIfCancellationRequested();

                var relPath = Path.GetRelativePath(directory, file.FullName);
                var depth = relPath.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

                if (maxDepth > 0 && depth > maxDepth)
                    continue;

                if (minDepth > 0 && depth < minDepth)
                    continue;

                result.Add(new GenericFileInfo
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    BasePath = directory,
                    Length = file.Length,
                    LastModified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                    Depth = depth
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return Task.FromResult(result);
    }

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
        => System.IO.File.ReadAllBytesAsync(path, ct);

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
        return Task.FromResult<Stream>(stream);
    }

    public Task WriteAsync(string path, byte[] data, CancellationToken ct)
        => System.IO.File.WriteAllBytesAsync(path, data, ct);

    public async Task WriteAsync(string path, Stream data, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await data.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    public async Task AppendAsync(string path, byte[] data, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await fs.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public async Task AppendTextAsync(string path, string text, Encoding encoding, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(fs, encoding);
        await writer.WriteAsync(text).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct)
        => Task.FromResult(System.IO.File.Exists(path));

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        System.IO.File.Delete(path);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct)
    {
        System.IO.File.Move(source, destination, overwrite);
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct)
        => Task.FromResult(Directory.Exists(path));

    public string CombinePath(string basePath, string relativePath)
        => Path.Combine(basePath, relativePath);

    public string GetParentPath(string path)
        => Path.GetDirectoryName(path) ?? path;

    public string GetFileName(string path)
        => Path.GetFileName(path);

    public string GetFileNameWithoutExtension(string name)
        => Path.GetFileNameWithoutExtension(name);

    public string GetExtension(string name)
        => Path.GetExtension(name);

    public string GetRelativePath(string basePath, string fullPath)
        => Path.GetRelativePath(basePath, fullPath);

    public bool IsAbsolutePath(string path)
        => Path.IsPathRooted(path);
}
