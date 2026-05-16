using IBM.WMQ;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.IbmMq;

/// <summary>
/// Deferred IBM MQ acknowledgement action. Wraps MQCMIT/MQBACK on the queue manager.
/// </summary>
internal sealed class IbmMqAckAction : ITransactedAction
{
    private readonly MQQueueManager _queueManager;
    private readonly ILogger? _logger;

    public IbmMqAckAction(MQQueueManager queueManager, ILogger? logger)
    {
        _queueManager = queueManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Commit(CancellationToken ct = default)
    {
        _queueManager.Commit();
        _logger?.LogDebug("IBM MQ ack committed (MQCMIT)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Rollback(CancellationToken ct = default)
    {
        _queueManager.Backout();
        _logger?.LogDebug("IBM MQ nack (MQBACK)");
        return Task.CompletedTask;
    }
}
