using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.GenericFile;

namespace redb.Route.File;

/// <summary>
/// File consumer. Inherits the complete poll loop from <see cref="GenericFileConsumer{TOptions}"/>
/// and adds local-file-specific behavior: read locking, lock-file exclusion, content-type inference.
/// </summary>
public class FileConsumer : GenericFileConsumer<FileEndpointOptions>
{
    private readonly FileEndpoint _endpoint;
    private readonly IReadLockStrategy _readLock;

    /// <inheritdoc />
    protected override string ConsumerName => $"file:{_endpoint.DirectoryPath}";

    /// <inheritdoc />
    protected override string BasePath => _endpoint.DirectoryPath;

    /// <summary>Creates a file consumer.</summary>
    public FileConsumer(FileEndpoint endpoint, IProcessor processor, FileEndpointOptions options)
        : base(endpoint, processor, options, new LocalFileOperations())
    {
        _endpoint = endpoint;
        _readLock = ReadLockFactory.Create(options.ReadLock);
    }

    /// <summary>The idempotent repository (null if idempotency is disabled).</summary>
    internal new InMemoryGenericFileIdempotentRepository? IdempotentRepository => base.IdempotentRepository;

    // ═══════════════════════════════════════════════════════════════════
    //  OVERRIDES
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    protected override IEnumerable<GenericFileInfo> ApplyFilters(IEnumerable<GenericFileInfo> files)
    {
        // Exclude lock/rename marker files (local-file-specific)
        files = files.Where(f =>
            !f.Name.EndsWith(Options.ReadLockMarkerFileExtension, StringComparison.OrdinalIgnoreCase) &&
            !f.Name.EndsWith(".redbRename", StringComparison.OrdinalIgnoreCase));

        return base.ApplyFilters(files);
    }

    /// <inheritdoc />
    protected override void SetExchangeHeaders(IMessage message, GenericFileInfo file, string workPath)
    {
        var ext = Operations.GetExtension(file.Name).ToLowerInvariant();
        message.ContentType = ext switch
        {
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".txt" or ".log" or ".csv" => "text/plain",
            ".html" or ".htm" => "text/html",
            _ => null
        };

        message.Headers[FileHeaders.FileName] = file.Name;
        message.Headers[FileHeaders.FileNameOnly] = Operations.GetFileNameWithoutExtension(file.Name);
        message.Headers[FileHeaders.FileExtension] = ext;
        message.Headers[FileHeaders.FileAbsolutePath] = workPath;
        message.Headers[FileHeaders.FileRelativePath] = Operations.GetRelativePath(file.BasePath, workPath);
        message.Headers[FileHeaders.FileParent] = Operations.GetParentPath(workPath);
        message.Headers[FileHeaders.FileLength] = file.Length;
        message.Headers[FileHeaders.FileLastModified] = file.LastModified;
    }

    /// <inheritdoc />
    protected override async Task<bool> AcquireReadLockAsync(GenericFileInfo file, CancellationToken ct)
    {
        var info = new FileInfo(file.FullPath);
        return await _readLock.AcquireLock(info, Options, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The rename strategy moves the file aside while it holds the lock.</remarks>
    protected override string ResolveWorkPath(GenericFileInfo file)
        => _readLock.GetWorkPath(file.FullPath);

    /// <inheritdoc />
    /// <remarks>
    /// When the strategy holds an exclusive handle (<see cref="ReadLockStrategy.FileLock"/>),
    /// the body is read through that handle. Opening the file a second time would be refused
    /// by the very lock this consumer holds.
    /// </remarks>
    protected override async Task<Exchange> CreateExchangeAsync(GenericFileInfo file, string workPath, CancellationToken ct)
    {
        var locked = _readLock.GetLockedStream(file.FullPath);
        if (locked == null)
            return await base.CreateExchangeAsync(file, workPath, ct).ConfigureAwait(false);

        if (locked.CanSeek)
            locked.Seek(0, SeekOrigin.Begin);

        object body;
        if (Options.StreamBody)
        {
            body = locked;
        }
        else
        {
            using var buffer = new MemoryStream();
            await locked.CopyToAsync(buffer, ct).ConfigureAwait(false);
            body = buffer.ToArray();
        }

        var message = new Message { Body = body };
        SetExchangeHeaders(message, file, workPath);

        return Exchange.Create(message, Endpoint.ScopeFactory);
    }

    /// <inheritdoc />
    protected override Task ReleaseReadLockAsync(GenericFileInfo file, CancellationToken ct)
    {
        var info = new FileInfo(file.FullPath);
        _readLock.ReleaseLock(info, Options);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Moves a failed file to <see cref="FileEndpointOptions.MoveFailed"/> when configured, so a poison
    /// file is quarantined out of the poll directory instead of being re-picked on every poll. No-op when
    /// unset (the file is left in place). Best-effort: a move failure is logged, not thrown.
    /// </remarks>
    protected override async Task OnProcessingFailedAsync(string filePath, string fileName, string basePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Options.MoveFailed))
            return;

        try
        {
            var failedDir = ResolveDirectory(
                GenericFileUtils.SubstituteFileTokens(fileName, Options.MoveFailed, Operations), basePath);
            await Operations.CreateDirectoryAsync(failedDir, ct).ConfigureAwait(false);

            var targetPath = Operations.CombinePath(failedDir, fileName);

            if (await Operations.ExistsAsync(targetPath, ct).ConfigureAwait(false))
            {
                try
                {
                    await Operations.DeleteAsync(targetPath, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning(ex, "{Consumer}: failed to delete target before move-failed {Path}", ConsumerName, targetPath);
                }
            }

            await Operations.MoveAsync(filePath, targetPath, false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "{Consumer}: failed to move file to failed directory {File}", ConsumerName, fileName);
        }
    }

    /// <summary>
    /// Simple glob matching supporting * and ? wildcards.
    /// Converts glob to regex for matching.
    /// </summary>
    internal static bool GlobMatch(string input, string pattern)
        => GenericFileUtils.GlobMatch(input, pattern);
}
