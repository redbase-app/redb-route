namespace redb.Route.GenericFile;

/// <summary>
/// Abstraction for file system operations. Implementations adapt
/// concrete I/O APIs (System.IO, SSH.NET, FluentFTP) to a uniform interface.
/// All path operations are protocol-aware (forward-slash for remote, OS-native for local).
/// </summary>
public interface IFileOperations
{
    // ── Enumeration ─────────────────────────────────────────────────

    /// <summary>
    /// Lists files in the given directory.
    /// </summary>
    /// <param name="directory">Base directory path.</param>
    /// <param name="recursive">Whether to recurse into subdirectories.</param>
    /// <param name="maxDepth">Maximum recursion depth (0 = unlimited).</param>
    /// <param name="minDepth">Minimum depth — files at shallower levels are skipped.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of file metadata.</returns>
    Task<List<GenericFileInfo>> ListFilesAsync(
        string directory, bool recursive, int maxDepth, int minDepth,
        CancellationToken ct = default);

    // ── Read ────────────────────────────────────────────────────────

    /// <summary>Reads the entire file into a byte array.</summary>
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default);

    /// <summary>Opens a read stream to the file. Caller is responsible for disposing.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    // ── Write ───────────────────────────────────────────────────────

    /// <summary>Writes data to a file, creating or overwriting it.</summary>
    Task WriteAsync(string path, byte[] data, CancellationToken ct = default);

    /// <summary>Writes a stream to a file, creating or overwriting it.</summary>
    Task WriteAsync(string path, Stream data, CancellationToken ct = default);

    /// <summary>Appends data to an existing file.</summary>
    Task AppendAsync(string path, byte[] data, CancellationToken ct = default);

    /// <summary>Appends text to an existing file.</summary>
    Task AppendTextAsync(string path, string text, System.Text.Encoding encoding, CancellationToken ct = default);

    // ── File operations ─────────────────────────────────────────────

    /// <summary>Checks whether a file or directory exists.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>Deletes a file.</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>Moves (renames) a file from source to destination.</summary>
    Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct = default);

    // ── Directory operations ────────────────────────────────────────

    /// <summary>Creates a directory (and all parent directories if missing).</summary>
    Task CreateDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Checks whether a directory exists.</summary>
    Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default);

    // ── Path helpers ────────────────────────────────────────────────

    /// <summary>Combines a base path and a relative path using the appropriate separator.</summary>
    string CombinePath(string basePath, string relativePath);

    /// <summary>Returns the parent directory of the given path.</summary>
    string GetParentPath(string path);

    /// <summary>Returns the file name from a path.</summary>
    string GetFileName(string path);

    /// <summary>Returns the file name without extension.</summary>
    string GetFileNameWithoutExtension(string name);

    /// <summary>Returns the extension including the dot (e.g. ".csv").</summary>
    string GetExtension(string name);

    /// <summary>Returns the relative path from basePath to fullPath.</summary>
    string GetRelativePath(string basePath, string fullPath);

    /// <summary>Checks whether the given path is absolute.</summary>
    bool IsAbsolutePath(string path);
}

/// <summary>
/// Extension of <see cref="IFileOperations"/> for remote file systems (SFTP, FTP).
/// Adds connection lifecycle management.
/// </summary>
public interface IRemoteFileOperations : IFileOperations
{
    /// <summary>Whether a connection is currently established.</summary>
    bool IsConnected { get; }

    /// <summary>Establishes a connection to the remote server.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Disconnects from the remote server.</summary>
    Task DisconnectAsync(CancellationToken ct = default);
}
