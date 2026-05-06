using Azure.Messaging.ServiceBus;
using redb.Route.Abstractions;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// Deferred complete/abandon for non-session transacted consumer.
/// Registered via <c>exchange.Properties["TRANSACT_ACTION"]</c>.
/// </summary>
internal sealed class AzureServiceBusAckAction : ITransactedAction
{
    private readonly ProcessMessageEventArgs _args;

    internal AzureServiceBusAckAction(ProcessMessageEventArgs args)
    {
        _args = args;
    }

    /// <inheritdoc />
    public async Task Commit(CancellationToken ct = default)
    {
        await _args.CompleteMessageAsync(_args.Message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Rollback(CancellationToken ct = default)
    {
        await _args.AbandonMessageAsync(_args.Message, cancellationToken: ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Deferred complete/abandon for session-aware transacted consumer.
/// </summary>
internal sealed class AzureServiceBusSessionAckAction : ITransactedAction
{
    private readonly ProcessSessionMessageEventArgs _args;

    internal AzureServiceBusSessionAckAction(ProcessSessionMessageEventArgs args)
    {
        _args = args;
    }

    /// <inheritdoc />
    public async Task Commit(CancellationToken ct = default)
    {
        await _args.CompleteMessageAsync(_args.Message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Rollback(CancellationToken ct = default)
    {
        await _args.AbandonMessageAsync(_args.Message, cancellationToken: ct).ConfigureAwait(false);
    }
}
