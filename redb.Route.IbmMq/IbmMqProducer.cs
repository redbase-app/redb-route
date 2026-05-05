using System.Collections.Concurrent;
using System.Diagnostics;
using IBM.WMQ;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using RouteMessage = redb.Route.Core.Message;

namespace redb.Route.IbmMq;

/// <summary>
/// IBM MQ producer. Sends messages to a queue or topic via MQPUT with support for:
/// <list type="bullet">
///   <item><b>Immediate</b> — direct MQPUT.</item>
///   <item><b>Transactional</b> — deferred send via <see cref="ITransactedAction"/>.</item>
///   <item><b>RPC</b> — request/reply with correlation matching.</item>
/// </list>
/// </summary>
public sealed class IbmMqProducer : ConnectableProducer
{
    private readonly IbmMqEndpoint _endpoint;
    private readonly IbmMqEndpointOptions _options;

    private MQQueue? _outputQueue;
    private MQTopic? _outputTopic;

    // ── RPC infrastructure ──
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IMessage>> _pendingResponses = new();
    private MQQueue? _replyQueue;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private volatile bool _rpcSetup;
    private CancellationTokenSource? _rpcCts;
    private Task? _rpcTask;

    /// <summary>Creates an IBM MQ producer.</summary>
    public IbmMqProducer(IbmMqEndpoint endpoint, IbmMqEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"wmq:{_endpoint.Destination}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        if (_options.DestinationType == IbmMqDestinationType.Topic)
        {
            _outputTopic = await _endpoint.OpenTopicForPublishAsync(
                _endpoint.Destination,
                MQC.MQOO_OUTPUT | MQC.MQOO_FAIL_IF_QUIESCING,
                ct).ConfigureAwait(false);
        }
        else
        {
            var openOptions = MQC.MQOO_OUTPUT | MQC.MQOO_FAIL_IF_QUIESCING;
            _outputQueue = await _endpoint.OpenQueueAsync(openOptions, ct).ConfigureAwait(false);
        }

        if (_options.ReplyTo)
            await SetupReplyReceiverAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task DisconnectAsync(CancellationToken ct)
    {
        foreach (var pending in _pendingResponses)
            pending.Value.TrySetCanceled(ct);
        _pendingResponses.Clear();

        _rpcCts?.Cancel();

        if (_rpcTask != null)
        {
            try { await _rpcTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _rpcTask = null;
        }

        _rpcCts?.Dispose();
        _rpcCts = null;

        if (_replyQueue != null)
        {
            try { _replyQueue.Close(); }
            catch (Exception ex) { Logger?.LogDebug(ex, "IBM MQ: error closing reply queue during stop"); }
            _replyQueue = null;
            _rpcSetup = false;
        }

        if (_outputQueue != null)
        {
            try { _outputQueue.Close(); }
            catch (Exception ex) { Logger?.LogDebug(ex, "IBM MQ: error closing output queue during stop"); }
            _outputQueue = null;
        }

        if (_outputTopic != null)
        {
            try { _outputTopic.Close(); }
            catch (Exception ex) { Logger?.LogDebug(ex, "IBM MQ: error closing output topic during stop"); }
            _outputTopic = null;
        }
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        using var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.Destination} publish", ActivityKind.Producer);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "wmq");
            activity.SetTag("messaging.operation", "publish");
            activity.SetTag("messaging.destination.name", _endpoint.Destination);
            activity.SetTag("messaging.ibmmq.queue_manager", _options.QueueManager);
        }

        var msg = IbmMqMessageHelper.BuildOutgoingMessage(exchange, _options);

        // Inject W3C trace context into MQ message properties (RFH2 usr folder)
        InjectTraceContext(activity, msg);

        if (_options.ReplyTo)
            await ProcessRpcAsync(exchange, msg, ct).ConfigureAwait(false);
        else if (_options.Transacted)
            ProcessTransactional(exchange, msg);
        else
            ProcessImmediate(msg);
    }

    // ── Immediate send ──

    private void ProcessImmediate(MQMessage msg)
    {
        var pmo = new MQPutMessageOptions
        {
            Options = MQC.MQPMO_NO_SYNCPOINT | MQC.MQPMO_FAIL_IF_QUIESCING
        };

        PutMessage(msg, pmo);
        Logger?.LogDebug("IBM MQ immediate send: destination={Destination}", _endpoint.Destination);
    }

    // ── Transactional deferred send ──

    private void ProcessTransactional(IExchange exchange, MQMessage msg)
    {
        // Capture all data needed for deferred send — the action will MQPUT + MQCMIT on commit
        var qm = _endpoint.GetQueueManagerAsync().GetAwaiter().GetResult();
        var action = new IbmMqSendAction(
            _outputQueue, _outputTopic, _options.DestinationType,
            msg, _endpoint.Destination, qm, Logger);

        RegisterTransactedAction(exchange, $"ibmmq-send-{Guid.NewGuid():N}", action);
    }

    // ── RPC (request/reply) ──

    private async Task ProcessRpcAsync(IExchange exchange, MQMessage msg, CancellationToken ct)
    {
        if (!_rpcSetup)
            await SetupReplyReceiverAsync(ct).ConfigureAwait(false);

        // Set up reply-to
        var replyToQueue = _options.ReplyToQueue;
        if (string.IsNullOrEmpty(replyToQueue) && _replyQueue != null)
            replyToQueue = _replyQueue.Name.Trim();

        msg.ReplyToQueueName = replyToQueue ?? string.Empty;
        if (!string.IsNullOrEmpty(_options.ReplyToQueueManager))
            msg.ReplyToQueueManagerName = _options.ReplyToQueueManager;

        msg.MessageType = MQC.MQMT_REQUEST;

        // Put the message
        var pmo = new MQPutMessageOptions
        {
            Options = MQC.MQPMO_NO_SYNCPOINT | MQC.MQPMO_FAIL_IF_QUIESCING
        };
        PutMessage(msg, pmo);

        // The correlation key depends on the pattern
        var correlKey = _options.CorrelationPattern == IbmMqCorrelationPattern.CorrelId
            ? IbmMqMessageHelper.BytesToHex(msg.CorrelationId)
            : IbmMqMessageHelper.BytesToHex(msg.MessageId);

        var tcs = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[correlKey] = tcs;

        Logger?.LogDebug("IBM MQ RPC request sent: correlKey={CorrelKey}, replyTo={ReplyTo}",
            correlKey, replyToQueue);

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.Timeout));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var responseTask = tcs.Task;
            var completed = await Task.WhenAny(responseTask, Task.Delay(Timeout.Infinite, linked.Token))
                .ConfigureAwait(false);

            if (completed != responseTask)
            {
                _pendingResponses.TryRemove(correlKey, out _);
                throw new TimeoutException(
                    $"IBM MQ RPC timeout ({_options.Timeout}s): destination={_endpoint.Destination}");
            }

            var response = await responseTask.ConfigureAwait(false);
            exchange.Out = response;
            exchange.Pattern = ExchangePattern.InOut;

            Logger?.LogDebug("IBM MQ RPC response received: correlKey={CorrelKey}", correlKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _pendingResponses.TryRemove(correlKey, out _);
            Logger?.LogError(ex, "IBM MQ RPC timeout/error: destination={Destination}, correlKey={CorrelKey}",
                _endpoint.Destination, correlKey);
            throw;
        }
    }

    // ── RPC reply receiver setup ──

    private async Task SetupReplyReceiverAsync(CancellationToken ct)
    {
        if (_rpcSetup) return;

        await _rpcLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rpcSetup) return;

            var replyQueueName = _options.ReplyToQueue;

            if (string.IsNullOrEmpty(replyQueueName))
            {
                // Use a model queue to create a dynamic temporary reply queue
                var qm = await _endpoint.GetQueueManagerAsync(ct).ConfigureAwait(false);
                _replyQueue = qm.AccessQueue(
                    "SYSTEM.DEFAULT.MODEL.QUEUE",
                    MQC.MQOO_INPUT_SHARED | MQC.MQOO_FAIL_IF_QUIESCING,
                    qm.Name,
                    "REDB.RPL.*",
                    string.Empty);

                replyQueueName = _replyQueue.Name.Trim();
            }
            else
            {
                // Open an existing reply queue
                var qm = await _endpoint.GetQueueManagerAsync(ct).ConfigureAwait(false);
                _replyQueue = qm.AccessQueue(
                    replyQueueName,
                    MQC.MQOO_INPUT_SHARED | MQC.MQOO_FAIL_IF_QUIESCING);
            }

            _rpcCts = new CancellationTokenSource();
            _rpcTask = Task.Run(() => RpcReceiveLoopAsync(_rpcCts.Token), _rpcCts.Token);

            _rpcSetup = true;
            Logger?.LogDebug("IBM MQ RPC reply receiver created: replyQueue={ReplyQueue}", replyQueueName);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    private async Task RpcReceiveLoopAsync(CancellationToken ct)
    {
        var gmo = new MQGetMessageOptions
        {
            Options = MQC.MQGMO_WAIT | MQC.MQGMO_NO_SYNCPOINT |
                      MQC.MQGMO_FAIL_IF_QUIESCING | MQC.MQGMO_CONVERT,
            WaitInterval = 2000,
        };

        while (!ct.IsCancellationRequested && _replyQueue != null)
        {
            try
            {
                var replyMsg = new MQMessage();

                try
                {
                    _replyQueue.Get(replyMsg, gmo);
                }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE)
                {
                    continue;
                }

                var correlKey = _options.CorrelationPattern == IbmMqCorrelationPattern.CorrelId
                    ? IbmMqMessageHelper.BytesToHex(replyMsg.CorrelationId)
                    : IbmMqMessageHelper.BytesToHex(replyMsg.CorrelationId); // Reply always has correlId

                if (_pendingResponses.TryRemove(correlKey, out var tcs))
                {
                    try
                    {
                        var body = IbmMqMessageHelper.ExtractBody(replyMsg);
                        var response = new RouteMessage(body);

                        if (_options.MqmdReadEnabled)
                            IbmMqMessageHelper.CopyMqmdToHeaders(replyMsg, response,
                                _replyQueue.Name.Trim(), _options.QueueManager);

                        tcs.TrySetResult(response);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }
            }
            catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_Q_MGR_QUIESCING ||
                                          ex.ReasonCode == MQC.MQRC_CONNECTION_BROKEN)
            {
                if (!ct.IsCancellationRequested)
                    Logger?.LogWarning("IBM MQ RPC reply connection interrupted: RC={ReasonCode}", ex.ReasonCode);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in IBM MQ RPC reply receive loop");
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // ── Message put ──

    private void PutMessage(MQMessage msg, MQPutMessageOptions pmo)
    {
        if (_options.DestinationType == IbmMqDestinationType.Topic)
            _outputTopic!.Put(msg, pmo);
        else
            _outputQueue!.Put(msg, pmo);
    }

    // ── Trace context ──

    private static void InjectTraceContext(Activity? activity, MQMessage msg)
    {
        if (activity is null) return;

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, msg, static (carrier, key, value) =>
        {
            if (carrier is MQMessage mqMsg && !string.IsNullOrEmpty(value))
            {
                try { mqMsg.SetStringProperty(key, value); }
                catch { /* Skip if property name is invalid */ }
            }
        });
    }

    // ── Helpers ──

    private static void RegisterTransactedAction(IExchange exchange, string key, ITransactedAction action)
    {
        if (!exchange.Properties.TryGetValue("TRANSACT_ACTION", out var raw) ||
            raw is not ConcurrentDictionary<string, ITransactedAction> dict)
        {
            dict = new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase);
            exchange.Properties["TRANSACT_ACTION"] = dict;
        }

        dict[key] = action;
    }
}
