using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.GenericFile;

/// <summary>
/// Base class for remote file consumers (SFTP, FTP). Extends <see cref="GenericFileConsumer{TOptions}"/>
/// with connection lifecycle management: auto-connect before poll, reconnect on failure,
/// disconnect after poll if configured, and remote-specific features (MaxAge, MoveFailed, SendEmptyMessageWhenIdle).
/// </summary>
/// <typeparam name="TOptions">Concrete options type inheriting <see cref="RemoteFileEndpointOptions"/>.</typeparam>
public abstract class RemoteFileConsumer<TOptions> : GenericFileConsumer<TOptions>
    where TOptions : RemoteFileEndpointOptions
{
    private readonly IRemoteFileOperations _remoteOps;

    /// <summary>Remote file operations with connection lifecycle.</summary>
    protected IRemoteFileOperations RemoteOperations => _remoteOps;

    /// <summary>Creates a remote file consumer.</summary>
    protected RemoteFileConsumer(IEndpoint endpoint, IProcessor processor, TOptions options, IRemoteFileOperations operations)
        : base(endpoint, processor, options, operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _remoteOps = operations;
    }

    /// <inheritdoc />
    protected override Task OnStopped()
    {
        DisconnectClient();
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  HOOKS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Connects to the remote server and validates the starting directory if required.
    /// </summary>
    protected override async Task BeforePollAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        if (Options.StartingDirectoryMustExist &&
            !await Operations.DirectoryExistsAsync(BasePath, ct).ConfigureAwait(false))
        {
            throw new DirectoryNotFoundException(
                $"Starting directory does not exist: {BasePath} on {Options.Host}:{Options.Port}");
        }
    }

    /// <summary>
    /// Disconnects from the remote server if the Disconnect option is set.
    /// </summary>
    protected override Task AfterPollAsync(List<GenericFileInfo> files, CancellationToken ct)
    {
        if (Options.Disconnect)
            DisconnectClient();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends an empty exchange when idle if <see cref="RemoteFileEndpointOptions.SendEmptyMessageWhenIdle"/> is set.
    /// </summary>
    protected override async Task OnNoFilesFoundAsync(CancellationToken ct)
    {
        if (!Options.SendEmptyMessageWhenIdle)
            return;

        var emptyExchange = Exchange.Create(new Message { Body = null }, ConsumerEndpoint.ScopeFactory);
        emptyExchange.Pattern = ExchangePattern.InOnly;
        try
        {
            await Processor.Process(emptyExchange, ct).ConfigureAwait(false);
        }
        finally
        {
            await emptyExchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Disconnects from the remote server on poll error to force a fresh connection on next poll.</summary>
    protected override void OnPollError(Exception ex)
    {
        DisconnectClient();
    }

    /// <summary>Checks maximum file age based on <see cref="RemoteFileEndpointOptions.MaxAge"/>.</summary>
    protected override bool CheckMaxAge(GenericFileInfo file)
    {
        if (Options.MaxAge <= 0)
            return true;

        var ageMs = (DateTimeOffset.UtcNow - file.LastModified).TotalMilliseconds;
        return ageMs <= Options.MaxAge;
    }

    /// <summary>Moves the file to the MoveFailed directory if configured.</summary>
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

    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensures a connection is active, reconnecting with retry if necessary.
    /// </summary>
    protected async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_remoteOps.IsConnected)
            return;

        var attempts = 0;
        while (true)
        {
            try
            {
                await _remoteOps.DisconnectAsync(ct).ConfigureAwait(false);
                await _remoteOps.ConnectAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (++attempts <= Options.MaximumReconnectAttempts)
            {
                Logger?.LogWarning(ex, "{Consumer} reconnect to {Host}:{Port} failed, attempt {Attempt}/{Max}",
                    ConsumerName, Options.Host, Options.Port, attempts, Options.MaximumReconnectAttempts);

                await Task.Delay(Options.ReconnectDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Safely disconnects from the remote server.
    /// </summary>
    protected void DisconnectClient()
    {
        try
        {
            if (_remoteOps.IsConnected)
                _remoteOps.DisconnectAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger?.LogDebug(ex, "{Consumer}: error during disconnect", ConsumerName);
        }
    }
}
