using IBM.WMQ;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.IbmMq;

/// <summary>
/// Deferred IBM MQ send action. Captures the message and destination,
/// executes MQPUT + MQCMIT on commit, or drops the message on rollback.
/// </summary>
internal sealed class IbmMqSendAction : ITransactedAction
{
    private readonly MQQueue? _queue;
    private readonly MQTopic? _topic;
    private readonly IbmMqDestinationType _destinationType;
    private readonly MQMessage _msg;
    private readonly string _destination;
    private readonly MQQueueManager _queueManager;
    private readonly ILogger? _logger;

    public IbmMqSendAction(
        MQQueue? queue,
        MQTopic? topic,
        IbmMqDestinationType destinationType,
        MQMessage msg,
        string destination,
        MQQueueManager queueManager,
        ILogger? logger)
    {
        _queue = queue;
        _topic = topic;
        _destinationType = destinationType;
        _msg = msg;
        _destination = destination;
        _queueManager = queueManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Commit(CancellationToken ct = default)
    {
        var pmo = new MQPutMessageOptions
        {
            Options = MQC.MQPMO_SYNCPOINT | MQC.MQPMO_FAIL_IF_QUIESCING
        };

        if (_destinationType == IbmMqDestinationType.Topic)
            _topic!.Put(_msg, pmo);
        else
            _queue!.Put(_msg, pmo);

        _queueManager.Commit();

        _logger?.LogDebug("IBM MQ transactional send committed: destination={Destination}", _destination);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("IBM MQ transactional send rolled back: destination={Destination}", _destination);
        return Task.CompletedTask;
    }
}
