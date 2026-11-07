using System.Collections.Concurrent;
using IBM.WMQ;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.IbmMq;

/// <summary>
/// The deferred puts of one producer in one transacted block. On commit every message is put under syncpoint and the
/// unit of work is committed once (MQCMIT), so the block's puts arrive together or not at all; a failed put backs the
/// unit of work out (MQBACK). On rollback nothing has been put.
/// </summary>
internal sealed class IbmMqSendBatch : ITransactedAction
{
    private readonly ConcurrentQueue<MQMessage> _messages = new();
    private readonly MQQueue? _queue;
    private readonly MQTopic? _topic;
    private readonly IbmMqDestinationType _destinationType;
    private readonly string _destination;
    private readonly MQQueueManager _queueManager;
    private readonly SemaphoreSlim _commitLock;
    private readonly ILogger? _logger;

    /// <param name="commitLock">
    /// The producer's lock: its connection is shared by every exchange, and a unit of work belongs to the connection, so
    /// one batch at a time puts and commits on it. Another batch's MQCMIT would otherwise commit half of this one.
    /// </param>
    public IbmMqSendBatch(
        MQQueue? queue,
        MQTopic? topic,
        IbmMqDestinationType destinationType,
        string destination,
        MQQueueManager queueManager,
        SemaphoreSlim commitLock,
        ILogger? logger)
    {
        _queue = queue;
        _topic = topic;
        _destinationType = destinationType;
        _destination = destination;
        _queueManager = queueManager;
        _commitLock = commitLock;
        _logger = logger;
    }

    /// <summary>Adds a message to the batch, after the ones the block deferred before it.</summary>
    public void Add(MQMessage message) => _messages.Enqueue(message);

    /// <inheritdoc />
    public async Task Commit(CancellationToken ct = default)
    {
        await _commitLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pmo = new MQPutMessageOptions
            {
                Options = MQC.MQPMO_SYNCPOINT | MQC.MQPMO_FAIL_IF_QUIESCING
            };

            try
            {
                foreach (var message in _messages)
                {
                    if (_destinationType == IbmMqDestinationType.Topic)
                        _topic!.Put(message, pmo);
                    else
                        _queue!.Put(message, pmo);
                }

                _queueManager.Commit();
            }
            catch (MQException)
            {
                // None of the batch may stand without the rest: back the unit of work out, then report the failure.
                BackOut();
                throw;
            }

            _logger?.LogDebug("IBM MQ transactional batch committed: destination={Destination}, messages={Count}",
                _destination, _messages.Count);
        }
        finally
        {
            _commitLock.Release();
        }
    }

    /// <inheritdoc />
    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("IBM MQ transactional batch rolled back: destination={Destination}, messages={Count}",
            _destination, _messages.Count);
        return Task.CompletedTask;
    }

    private void BackOut()
    {
        try
        {
            _queueManager.Backout();
        }
        catch (MQException backoutEx)
        {
            _logger?.LogWarning(backoutEx,
                "IBM MQ backout after a failed batch put failed: destination={Destination}. The unit of work ends with the " +
                "connection.", _destination);
        }
    }
}
