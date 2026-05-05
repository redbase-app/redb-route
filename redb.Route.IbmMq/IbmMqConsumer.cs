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
/// IBM MQ consumer. Polls messages from a queue (MQGET) or subscribes to a topic,
/// with support for concurrent processing, transacted ack, backout/dead-letter,
/// and W3C distributed tracing.
/// </summary>
public sealed class IbmMqConsumer : IConsumer
{
    private readonly IbmMqEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private readonly IbmMqEndpointOptions _options;
    private ILogger? _logger;
    private readonly SemaphoreSlim _semaphore;

    private MQQueue? _queue;
    private MQTopic? _topic;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly InflightDrainGuard _drain = new();

    /// <summary>Number of messages successfully processed.</summary>
    public long ProcessedCount { get; private set; }

    /// <summary>Creates an IBM MQ consumer.</summary>
    public IbmMqConsumer(IbmMqEndpoint endpoint, IProcessor processor, IbmMqEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = endpoint.Logger;
        _semaphore = new SemaphoreSlim(options.ConcurrentConsumers);
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        if (_options.DestinationType == IbmMqDestinationType.Topic)
        {
            _topic = await _endpoint.OpenTopicAsync(
                _endpoint.Destination,
                MQC.MQSO_CREATE | MQC.MQSO_NON_DURABLE | MQC.MQSO_MANAGED,
                ct).ConfigureAwait(false);
        }
        else
        {
            var openOptions = MQC.MQOO_INPUT_SHARED | MQC.MQOO_FAIL_IF_QUIESCING;
            if (_options.Transacted)
                openOptions |= MQC.MQOO_INPUT_SHARED;
            _queue = await _endpoint.OpenQueueAsync(openOptions, ct).ConfigureAwait(false);
        }

        _cts = new CancellationTokenSource();
        _drain.Start(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation(
            "IBM MQ consumer started: destination={Destination}, type={Type}, concurrent={Concurrent}",
            _endpoint.Destination, _options.DestinationType, _options.ConcurrentConsumers);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        _cts?.Cancel();

        await _drain.DrainAsync(ct, _logger, $"wmq:{_endpoint.Destination}").ConfigureAwait(false);

        if (_receiveTask != null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        CloseDestination();

        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _drain.Dispose();

        _logger?.LogInformation("IBM MQ consumer stopped: destination={Destination}", _endpoint.Destination);
    }

    // ── Receive loop ──

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var gmo = new MQGetMessageOptions
        {
            Options = MQC.MQGMO_WAIT | MQC.MQGMO_FAIL_IF_QUIESCING | MQC.MQGMO_PROPERTIES_IN_HANDLE,
            WaitInterval = _options.WaitInterval,
        };

        if (_options.Convert)
            gmo.Options |= MQC.MQGMO_CONVERT;

        if (_options.Transacted)
            gmo.Options |= MQC.MQGMO_SYNCPOINT;
        else
            gmo.Options |= MQC.MQGMO_NO_SYNCPOINT;

        if (!string.IsNullOrEmpty(_options.Selector))
            gmo.MatchOptions = MQC.MQMO_MATCH_MSG_ID;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var msg = new MQMessage();

                try
                {
                    if (_options.DestinationType == IbmMqDestinationType.Topic)
                        _topic!.Get(msg, gmo);
                    else
                        _queue!.Get(msg, gmo);
                }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE)
                {
                    // No message within WaitInterval — loop back
                    continue;
                }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_FORMAT_ERROR)
                {
                    // MQGMO_CONVERT can't convert binary (MQFMT_NONE) data — proceed with unconverted payload
                }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_Q_MGR_QUIESCING ||
                                              ex.ReasonCode == MQC.MQRC_CONNECTION_BROKEN)
                {
                    if (!ct.IsCancellationRequested)
                        _logger?.LogWarning("IBM MQ connection interrupted: RC={ReasonCode}", ex.ReasonCode);
                    break;
                }

                await _semaphore.WaitAsync(ct).ConfigureAwait(false);
                _drain.Increment();
                try
                {
                    await ProcessMessageAsync(msg, _drain.ProcessingToken).ConfigureAwait(false);
                }
                finally
                {
                    _drain.Decrement();
                    _semaphore.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in IBM MQ receive loop: destination={Destination}",
                    _endpoint.Destination);

                // Brief delay to avoid tight error loops
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessMessageAsync(MQMessage mqMsg, CancellationToken ct)
    {
        using var activity = StartConsumerActivity(mqMsg);

        var exchange = CreateExchange(mqMsg);

        // Register transacted ack action
        if (_options.Transacted)
        {
            var qm = await _endpoint.GetQueueManagerAsync(ct).ConfigureAwait(false);
            var ackAction = new IbmMqAckAction(qm, _logger);
            RegisterTransactedAction(exchange, $"ibmmq-ack-{Guid.NewGuid():N}", ackAction);
        }

        try
        {
            await _processor.Process(exchange, ct).ConfigureAwait(false);

            // RPC reply: if the incoming message had ReplyTo, send the Out message back
            if (!string.IsNullOrWhiteSpace(mqMsg.ReplyToQueueName))
            {
                await SendReplyAsync(exchange, mqMsg, ct).ConfigureAwait(false);
            }

            // Handle backout threshold
            if (_options.BackoutThreshold > 0 && mqMsg.BackoutCount >= _options.BackoutThreshold)
            {
                await MoveToBackoutQueueAsync(mqMsg, ct).ConfigureAwait(false);
            }

            // Commit in non-transacted mode is implicitly done by MQGET without syncpoint.
            // For transacted mode, commit/rollback is handled by IbmMqAckAction via route processor.

            ProcessedCount++;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "IBM MQ message processing error: destination={Destination}, msgId={MsgId}",
                _endpoint.Destination, IbmMqMessageHelper.BytesToHex(mqMsg.MessageId));

            if (_options.Transacted)
            {
                try
                {
                    var qm = await _endpoint.GetQueueManagerAsync(ct).ConfigureAwait(false);
                    qm.Backout();
                    _logger?.LogDebug("IBM MQ message rolled back after processing error");
                }
                catch (Exception rollbackEx)
                {
                    _logger?.LogWarning(rollbackEx, "Failed to rollback IBM MQ message");
                }
            }
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Trace context propagation ──

    private Activity? StartConsumerActivity(MQMessage mqMsg)
    {
        // Extract W3C trace context from MQ message properties (RFH2 usr folder)
        string? traceParent = null;
        string? traceState = null;

        try { traceParent = mqMsg.GetStringProperty("traceparent"); } catch { /* not present */ }
        try { traceState = mqMsg.GetStringProperty("tracestate"); } catch { /* not present */ }

        ActivityContext parentContext = default;
        if (!string.IsNullOrEmpty(traceParent))
            ActivityContext.TryParse(traceParent, traceState, out parentContext);

        var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.Destination} receive",
            ActivityKind.Consumer,
            parentContext);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "wmq");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", _endpoint.Destination);
            activity.SetTag("messaging.ibmmq.queue_manager", _options.QueueManager);

            if (mqMsg.MessageId is { Length: > 0 })
                activity.SetTag("messaging.message.id", IbmMqMessageHelper.BytesToHex(mqMsg.MessageId));
        }

        return activity;
    }

    // ── Exchange creation ──

    private Exchange CreateExchange(MQMessage mqMsg)
    {
        var body = IbmMqMessageHelper.ExtractBody(mqMsg);
        var routeMsg = new RouteMessage(body);

        // Map MQMD Format → ContentType
        routeMsg.ContentType = IbmMqMessageHelper.FormatToContentType(mqMsg.Format);

        // Copy MQMD → headers
        if (_options.MqmdReadEnabled)
            IbmMqMessageHelper.CopyMqmdToHeaders(mqMsg, routeMsg, _endpoint.Destination, _options.QueueManager);

        // Copy RFH2 user properties → headers
        IbmMqMessageHelper.CopyRfh2UserProperties(mqMsg, routeMsg);

        // Determine exchange pattern
        var hasReplyTo = !string.IsNullOrWhiteSpace(mqMsg.ReplyToQueueName);
        var isRequest = mqMsg.MessageType == MQC.MQMT_REQUEST;
        var pattern = hasReplyTo && isRequest ? ExchangePattern.InOut : ExchangePattern.InOnly;

        var exchange = Exchange.Create(routeMsg, _endpoint.ScopeFactory);
        exchange.Pattern = pattern;
        return exchange;
    }

    // ── RPC reply ──

    private async Task SendReplyAsync(IExchange exchange, MQMessage originalMsg, CancellationToken ct)
    {
        try
        {
            var responseBody = exchange.HasOut
                ? exchange.Out!.Body
                : exchange.In.Body;

            var reply = new MQMessage();

            if (responseBody is byte[] bytes)
            {
                reply.Write(bytes);
                reply.Format = MQC.MQFMT_NONE;
            }
            else
            {
                var text = responseBody?.ToString() ?? string.Empty;
                reply.WriteString(text);
                reply.Format = MQC.MQFMT_STRING;
            }

            reply.CorrelationId = originalMsg.MessageId;
            reply.MessageType = MQC.MQMT_REPLY;

            var replyQueueName = originalMsg.ReplyToQueueName.Trim();
            var replyQmName = originalMsg.ReplyToQueueManagerName?.Trim();

            var qm = await _endpoint.GetQueueManagerAsync(ct).ConfigureAwait(false);

            var openOptions = MQC.MQOO_OUTPUT | MQC.MQOO_FAIL_IF_QUIESCING;
            MQQueue replyQueue;
            if (!string.IsNullOrEmpty(replyQmName) && replyQmName != qm.Name.Trim())
                replyQueue = qm.AccessQueue(replyQueueName, openOptions, replyQmName, null, null);
            else
                replyQueue = qm.AccessQueue(replyQueueName, openOptions);

            try
            {
                var pmo = new MQPutMessageOptions { Options = MQC.MQPMO_NO_SYNCPOINT };
                replyQueue.Put(reply, pmo);

                _logger?.LogDebug(
                    "IBM MQ RPC reply sent: replyQueue={ReplyQueue}, correlationId={CorrelationId}",
                    replyQueueName, IbmMqMessageHelper.BytesToHex(reply.CorrelationId));
            }
            finally
            {
                try { replyQueue.Close(); }
                catch (Exception closeEx)
                {
                    _logger?.LogDebug(closeEx, "Error closing reply queue");
                }
            }
        }
        catch (Exception ex)
        {
            // Swallow: failed reply must not block the consumer slot. The original message
            // has already been destructively read (MQGMO_NO_SYNCPOINT) or will be committed
            // on the transactional path — the client will detect failure via its own RPC timeout.
            _logger?.LogWarning(ex, "IBM MQ: failed to send RPC reply to {ReplyQueue} — original message will still be settled",
                originalMsg.ReplyToQueueName?.Trim());
        }
    }

    // ── Backout queue ──

    private async Task MoveToBackoutQueueAsync(MQMessage mqMsg, CancellationToken ct)
    {
        var boqName = _options.BackoutQueue;
        if (string.IsNullOrWhiteSpace(boqName))
        {
            _logger?.LogWarning("Message exceeded backout threshold but no backout queue configured");
            return;
        }

        try
        {
            var qm = await _endpoint.GetQueueManagerAsync(ct).ConfigureAwait(false);
            using var boq = qm.AccessQueue(boqName, MQC.MQOO_OUTPUT | MQC.MQOO_FAIL_IF_QUIESCING);
            var pmo = new MQPutMessageOptions { Options = MQC.MQPMO_NO_SYNCPOINT };
            boq.Put(mqMsg, pmo);

            _logger?.LogInformation(
                "Poison message moved to backout queue: destination={Destination}, boq={BackoutQueue}, backoutCount={BackoutCount}",
                _endpoint.Destination, boqName, mqMsg.BackoutCount);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to move message to backout queue {BackoutQueue}", boqName);
        }
    }

    private void CloseDestination()
    {
        if (_queue != null)
        {
            try { _queue.Close(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Error closing IBM MQ queue"); }
            _queue = null;
        }

        if (_topic != null)
        {
            try { _topic.Close(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Error closing IBM MQ topic"); }
            _topic = null;
        }
    }

    // ── Transacted action registration ──

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
