using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.GenericFile;

/// <summary>
/// Base class for remote file producers (SFTP, FTP). Extends <see cref="GenericFileProducer{TOptions}"/>
/// with connection lifecycle management: auto-connect before write, reconnect on failure,
/// disconnect after write if configured.
/// </summary>
/// <typeparam name="TOptions">Concrete options type inheriting <see cref="RemoteFileEndpointOptions"/>.</typeparam>
public abstract class RemoteFileProducer<TOptions> : GenericFileProducer<TOptions>
    where TOptions : RemoteFileEndpointOptions
{
    private readonly IRemoteFileOperations _remoteOps;

    /// <summary>Remote file operations with connection lifecycle.</summary>
    protected IRemoteFileOperations RemoteOperations => _remoteOps;

    /// <summary>Creates a remote file producer.</summary>
    protected RemoteFileProducer(IEndpoint endpoint, TOptions options, IRemoteFileOperations operations)
        : base(endpoint, options, operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _remoteOps = operations;
    }

    /// <summary>
    /// Connects to the remote server and auto-creates the target directory if configured.
    /// </summary>
    protected override async Task BeforeWriteAsync(string targetDir, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await base.BeforeWriteAsync(targetDir, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Disconnects from the remote server if the Disconnect option is set.
    /// </summary>
    protected override Task OnWriteCompletedAsync(IExchange exchange, string targetPath, CancellationToken ct)
    {
        if (Options.Disconnect)
            DisconnectClient();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task Stop(CancellationToken ct = default)
    {
        DisconnectClient();
        await base.Stop(ct).ConfigureAwait(false);
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
                Logger?.LogWarning(ex, "Producer reconnect to {Host}:{Port} failed, attempt {Attempt}/{Max}",
                    Options.Host, Options.Port, attempts, Options.MaximumReconnectAttempts);

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
            Logger?.LogDebug(ex, "Producer: error during disconnect");
        }
    }
}
