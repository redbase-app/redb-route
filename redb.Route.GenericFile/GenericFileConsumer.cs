using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.GenericFile;

/// <summary>
/// Template-method base class for all file-based consumers (local file, SFTP, FTP).
/// Owns the complete poll loop: enumerate → filter → sort → limit → per-file processing
/// (min-age, done-file, idempotency, read-lock, pre-move, create exchange, post-process).
/// Subclasses provide protocol-specific hooks via virtual/abstract methods.
/// </summary>
/// <typeparam name="TOptions">Concrete options type inheriting <see cref="GenericFileEndpointOptions"/>.</typeparam>
public abstract class GenericFileConsumer<TOptions> : DrainableConsumer
    where TOptions : GenericFileEndpointOptions
{
    private readonly IEndpoint _endpoint;
    private readonly InMemoryGenericFileIdempotentRepository? _idempotentRepo;
    private long _processedCount;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <summary>Endpoint options.</summary>
    protected TOptions Options { get; }

    /// <summary>File operations (protocol-specific adapter).</summary>
    protected IFileOperations Operations { get; }

    /// <summary>Total number of files successfully processed.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>The idempotent repository (null if idempotency is disabled).</summary>
    internal InMemoryGenericFileIdempotentRepository? IdempotentRepository => _idempotentRepo;

    /// <summary>Base path for file operations (directory for local, remote path for SFTP/FTP).</summary>
    protected abstract string BasePath { get; }

    /// <summary>Creates a generic file consumer.</summary>
    protected GenericFileConsumer(IEndpoint endpoint, IProcessor processor, TOptions options, IFileOperations operations)
        : base(processor)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operations);
        _endpoint = endpoint;
        Options = options;
        Operations = operations;
        _idempotentRepo = options.Idempotent ? new InMemoryGenericFileIdempotentRepository() : null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  POLL LOOP (RunAsync + PollDirectory)
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        if (Options.InitialDelay > 0)
        {
            await Task.Delay(Options.InitialDelay, pollCt).ConfigureAwait(false);
        }

        var delay = TimeSpan.FromMilliseconds(Options.Delay);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                await PollDirectory(processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested || processingCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "{Consumer} poll failed", ConsumerName);
                OnPollError(ex);
            }

            try
            {
                await Task.Delay(delay, pollCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Polls the base directory for files, applies filters, and processes each file.
    /// This is the core template method — do not override.
    /// </summary>
    internal async Task PollDirectory(CancellationToken ct)
    {
        await BeforePollAsync(ct).ConfigureAwait(false);

        if (!await Operations.DirectoryExistsAsync(BasePath, ct).ConfigureAwait(false))
        {
            await AfterPollAsync([], ct).ConfigureAwait(false);
            return;
        }

        var files = await Operations.ListFilesAsync(
            BasePath, Options.Recursive, Options.MaxDepth, Options.MinDepth, ct).ConfigureAwait(false);

        var filtered = ApplyFilters(files);
        var sorted = ApplySort(filtered);
        var limited = ApplyMaxMessages(sorted);
        var fileList = limited.ToList();

        if (fileList.Count == 0)
        {
            await OnNoFilesFoundAsync(ct).ConfigureAwait(false);
            await AfterPollAsync(fileList, ct).ConfigureAwait(false);
            return;
        }

        foreach (var file in fileList)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessFileAsync(file, ct).ConfigureAwait(false);
        }

        await AfterPollAsync(fileList, ct).ConfigureAwait(false);
    }

    // ── Per-file processing ─────────────────────────────────────────

    private async Task ProcessFileAsync(GenericFileInfo file, CancellationToken ct)
    {
        // Check minimum age
        if (Options.MinAge > 0)
        {
            var ageMs = (DateTimeOffset.UtcNow - file.LastModified).TotalMilliseconds;
            if (ageMs < Options.MinAge)
                return;
        }

        // Check maximum age (virtual — remote connectors override)
        if (!CheckMaxAge(file))
            return;

        // Check done file
        if (!string.IsNullOrEmpty(Options.DoneFileName))
        {
            var doneFilePath = GenericFileUtils.ResolveDoneFileName(file, Options.DoneFileName, Operations);
            if (!await Operations.ExistsAsync(doneFilePath, ct).ConfigureAwait(false))
                return;
        }

        // Check idempotency
        var idempotentKey = "";
        if (_idempotentRepo != null)
        {
            idempotentKey = string.IsNullOrEmpty(Options.IdempotentKey)
                ? InMemoryGenericFileIdempotentRepository.DefaultKey(file)
                : Options.IdempotentKey;

            if (!await _idempotentRepo.Add(idempotentKey, ct).ConfigureAwait(false))
                return; // Already processed
        }

        // Acquire read lock (virtual — local file only)
        if (!await AcquireReadLockAsync(file, ct).ConfigureAwait(false))
            return;

        try
        {
            // PreMove
            var workPath = file.FullPath;
            if (!string.IsNullOrEmpty(Options.PreMove))
            {
                workPath = await PreMoveFileAsync(file, ct).ConfigureAwait(false);
            }

            // Create exchange with body and protocol-specific headers
            var exchange = await CreateExchangeAsync(file, workPath, ct).ConfigureAwait(false);
            exchange.Pattern = ExchangePattern.InOnly;

            IncrementInflight();
            try
            {
                await Processor.Process(exchange, ct).ConfigureAwait(false);
            }
            finally
            {
                DecrementInflight();
                await exchange.DisposeAsync().ConfigureAwait(false);
            }

            if (exchange.Exception != null && !exchange.ExceptionHandled)
            {
                // Processing failed — remove idempotent key so it can be retried
                if (_idempotentRepo != null)
                    await _idempotentRepo.Remove(idempotentKey, ct).ConfigureAwait(false);

                // Move to failed directory (virtual hook — remote connectors)
                await OnProcessingFailedAsync(workPath, file.Name, file.BasePath, ct).ConfigureAwait(false);
                return;
            }

            // Post-processing
            await PostProcessAsync(workPath, file.Name, file.BasePath, ct).ConfigureAwait(false);

            // Confirm idempotent key
            if (_idempotentRepo != null)
                await _idempotentRepo.Confirm(idempotentKey, ct).ConfigureAwait(false);

            // Delete done file if present
            if (!string.IsNullOrEmpty(Options.DoneFileName))
            {
                var donePath = GenericFileUtils.ResolveDoneFileName(file, Options.DoneFileName, Operations);
                try
                {
                    await Operations.DeleteAsync(donePath, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning(ex, "{Consumer}: failed to delete done file {Path}", ConsumerName, donePath);
                }
            }

            Interlocked.Increment(ref _processedCount);
        }
        catch when (_idempotentRepo != null && !string.IsNullOrEmpty(idempotentKey))
        {
            await _idempotentRepo.Remove(idempotentKey, ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await ReleaseReadLockAsync(file, ct).ConfigureAwait(false);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SHARED LOGIC (non-virtual)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies Include/Exclude glob filters and excludes internal temp files.
    /// Override to add protocol-specific exclusions (e.g. lock files).
    /// </summary>
    protected virtual IEnumerable<GenericFileInfo> ApplyFilters(IEnumerable<GenericFileInfo> files)
    {
        // Exclude internal temp files
        files = files.Where(f => !f.Name.StartsWith(".redb_", StringComparison.OrdinalIgnoreCase));

        // Exclude TempPrefix-prefixed files
        if (!string.IsNullOrEmpty(Options.TempPrefix))
        {
            files = files.Where(f => !f.Name.StartsWith(Options.TempPrefix, StringComparison.OrdinalIgnoreCase));
        }

        // Include filter (glob)
        if (!string.IsNullOrEmpty(Options.Include))
        {
            files = files.Where(f => GenericFileUtils.GlobMatch(f.Name, Options.Include));
        }

        // Exclude filter (glob)
        if (!string.IsNullOrEmpty(Options.Exclude))
        {
            files = files.Where(f => !GenericFileUtils.GlobMatch(f.Name, Options.Exclude));
        }

        return files;
    }

    private IEnumerable<GenericFileInfo> ApplySort(IEnumerable<GenericFileInfo> files) => Options.SortBy switch
    {
        GenericFileSortBy.Name => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        GenericFileSortBy.NameDesc => files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),
        GenericFileSortBy.Modified => files.OrderBy(f => f.LastModified),
        GenericFileSortBy.ModifiedDesc => files.OrderByDescending(f => f.LastModified),
        GenericFileSortBy.Size => files.OrderBy(f => f.Length),
        GenericFileSortBy.SizeDesc => files.OrderByDescending(f => f.Length),
        _ => files
    };

    private IEnumerable<GenericFileInfo> ApplyMaxMessages(IEnumerable<GenericFileInfo> files)
    {
        if (Options.MaxMessagesPerPoll > 0)
            return files.Take(Options.MaxMessagesPerPoll);
        return files;
    }

    // ── Pre/Post processing ─────────────────────────────────────────

    /// <summary>
    /// Moves a file to the PreMove directory before processing.
    /// Returns the new path of the file. Override for protocol-specific behavior.
    /// </summary>
    protected virtual async Task<string> PreMoveFileAsync(GenericFileInfo file, CancellationToken ct)
    {
        var preMoveDir = ResolveDirectory(Options.PreMove, file.BasePath);
        await Operations.CreateDirectoryAsync(preMoveDir, ct).ConfigureAwait(false);

        var targetPath = Operations.CombinePath(preMoveDir, file.Name);

        // Delete existing file at target if present
        if (await Operations.ExistsAsync(targetPath, ct).ConfigureAwait(false))
        {
            try
            {
                await Operations.DeleteAsync(targetPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "{Consumer}: failed to delete existing file before pre-move {Path}", ConsumerName, targetPath);
            }
        }

        await Operations.MoveAsync(file.FullPath, targetPath, true, ct).ConfigureAwait(false);
        return targetPath;
    }

    /// <summary>
    /// Applies the configured post-processing strategy (noop/delete/moveTo).
    /// </summary>
    protected virtual async Task PostProcessAsync(string filePath, string fileName, string basePath, CancellationToken ct)
    {
        if (Options.Noop)
            return;

        if (Options.Delete)
        {
            try
            {
                await Operations.DeleteAsync(filePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "{Consumer}: failed to delete processed file {Path}", ConsumerName, filePath);
            }
            return;
        }

        if (!string.IsNullOrEmpty(Options.MoveTo))
        {
            var moveDir = ResolveDirectory(Options.MoveTo, basePath);
            await Operations.CreateDirectoryAsync(moveDir, ct).ConfigureAwait(false);

            var targetPath = Operations.CombinePath(moveDir, fileName);

            // Handle existing file at target
            if (await Operations.ExistsAsync(targetPath, ct).ConfigureAwait(false))
            {
                switch (Options.MoveExisting)
                {
                    case GenericFileExistStrategy.Override:
                        try
                        {
                            await Operations.DeleteAsync(targetPath, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogWarning(ex, "{Consumer}: failed to delete target before move (Override) {Path}", ConsumerName, targetPath);
                        }
                        break;

                    case GenericFileExistStrategy.Fail:
                        throw new IOException($"Target file already exists: {targetPath}");

                    case GenericFileExistStrategy.Ignore:
                        return;

                    default:
                        try
                        {
                            await Operations.DeleteAsync(targetPath, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogWarning(ex, "{Consumer}: failed to delete target before move (default) {Path}", ConsumerName, targetPath);
                        }
                        break;
                }
            }

            await Operations.MoveAsync(filePath, targetPath, false, ct).ConfigureAwait(false);
        }
    }

    // ── Exchange creation ───────────────────────────────────────────

    /// <summary>
    /// Creates an exchange with the file body and protocol-specific headers.
    /// </summary>
    /// <param name="file">Original file info (for metadata).</param>
    /// <param name="workPath">Current file path (may differ from file.FullPath after pre-move).</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual async Task<Exchange> CreateExchangeAsync(GenericFileInfo file, string workPath, CancellationToken ct)
    {
        object body;
        try
        {
            if (Options.StreamBody)
            {
                body = await Operations.OpenReadAsync(workPath, ct).ConfigureAwait(false);
            }
            else
            {
                body = await Operations.ReadAllBytesAsync(workPath, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "{Consumer}: failed to read {Path}, using empty content", ConsumerName, workPath);
            body = Array.Empty<byte>();
        }

        var message = new Message { Body = body };
        SetExchangeHeaders(message, file, workPath);

        return Exchange.Create(message, _endpoint.ScopeFactory);
    }

    /// <summary>
    /// Sets protocol-specific headers on the exchange message.
    /// Each connector sets headers with its own prefix (redbFile.*, redbSftp.*, redbFtp.*).
    /// </summary>
    /// <param name="message">Message to set headers on.</param>
    /// <param name="file">Original file metadata (name, extension, base path, etc.).</param>
    /// <param name="workPath">Current file location — may differ from file.FullPath after pre-move.</param>
    protected abstract void SetExchangeHeaders(IMessage message, GenericFileInfo file, string workPath);

    // ═══════════════════════════════════════════════════════════════════
    //  VIRTUAL HOOKS (overridden by subclasses)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called before each poll cycle. Override to connect to remote server,
    /// validate directory existence, etc.
    /// </summary>
    protected virtual Task BeforePollAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Called after each poll cycle with the list of files that were polled.
    /// Override to disconnect if configured, etc.
    /// </summary>
    protected virtual Task AfterPollAsync(List<GenericFileInfo> files, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Called when a poll cycle finds no files. Override to send heartbeat exchanges.
    /// </summary>
    protected virtual Task OnNoFilesFoundAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Called when PollDirectory catches a non-cancellation exception.
    /// Override to disconnect from remote server after errors.
    /// </summary>
    protected virtual void OnPollError(Exception ex) { }

    /// <summary>
    /// Checks maximum file age. Returns true if the file should be processed.
    /// Override in remote connectors to implement MaxAge filtering.
    /// </summary>
    protected virtual bool CheckMaxAge(GenericFileInfo file) => true;

    /// <summary>
    /// Acquires a read lock on the file before processing. Returns true if lock was acquired.
    /// Override in local file consumer for read-lock strategies.
    /// </summary>
    protected virtual Task<bool> AcquireReadLockAsync(GenericFileInfo file, CancellationToken ct)
        => Task.FromResult(true);

    /// <summary>
    /// Releases the read lock after processing. Override in local file consumer.
    /// </summary>
    protected virtual Task ReleaseReadLockAsync(GenericFileInfo file, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Called when processing fails (exchange.Exception is set and not handled).
    /// Override in remote connectors to move file to failure directory.
    /// </summary>
    protected virtual Task OnProcessingFailedAsync(string filePath, string fileName, string basePath, CancellationToken ct)
        => Task.CompletedTask;

    // ═══════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves a directory path: if absolute, returns as-is; if relative, combines with basePath.
    /// </summary>
    protected string ResolveDirectory(string dir, string basePath)
    {
        if (Operations.IsAbsolutePath(dir))
            return dir;
        return Operations.CombinePath(basePath, dir);
    }

    /// <summary>
    /// Resolves encoding from the <see cref="GenericFileEndpointOptions.Charset"/> option with UTF-8 fallback.
    /// </summary>
    protected Encoding ResolveEncoding()
    {
        try
        {
            return Encoding.GetEncoding(Options.Charset);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
