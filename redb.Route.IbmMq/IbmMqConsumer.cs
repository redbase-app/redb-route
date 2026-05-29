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
    private MQQueueManager? _qm;
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
        // Each consumer owns its own MQQueueManager to avoid MQI-call serialisation
        // deadlocks with producers / RPC reply loops on the same broker connection.
        _qm = await _endpoint.CreateDedicatedQueueManagerAsync($"consumer:{_endpoint.Destination}", ct).ConfigureAwait(false);

        if (_options.DestinationType == IbmMqDestinationType.Topic)
        {
            var subName = $"REDB.{_endpoint.Destination}.{Guid.NewGuid():N}";
            _topic = _qm.AccessTopic(
                _endpoint.Destination, null,
                MQC.MQSO_CREATE | MQC.MQSO_NON_DURABLE | MQC.MQSO_MANAGED,
                null, subName);
        }
        else
        {
            var openOptions = MQC.MQOO_INPUT_SHARED | MQC.MQOO_FAIL_IF_QUIESCING;
            _queue = _qm.AccessQueue(_endpoint.Destination, openOptions);
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

        if (_qm != null)
        {
            try { if (_qm.IsConnected) _qm.Disconnect(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ: error disconnecting consumer queue manager during stop"); }
            _qm = null;
        }

        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _drain.Dispose();

        _logger?.LogInformation("IBM MQ consumer stopped: destination={Destination}", _endpoint.Destination);
    }

    // ── Receive loop ──

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // Note: MQGMO_PROPERTIES_IN_HANDLE requires gmo.MessageHandle to be set via
        // qm.CreateMessageHandle(). Without it, GetStringProperty calls in helpers can
        // misbehave on the managed .NET client. Use queue-default property handling.
        var gmo = new MQGetMessageOptions
        {
            Options = MQC.MQGMO_WAIT | MQC.MQGMO_FAIL_IF_QUIESCING,
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
                var getSw = Stopwatch.StartNew();
                bool gotMessage = false;

                try
                {
                    if (_options.DestinationType == IbmMqDestinationType.Topic)
                        _topic!.Get(msg, gmo);
                    else
                        _queue!.Get(msg, gmo);
                    gotMessage = true;
                }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE)
                {
                    // No message within WaitInterval — loop back
                    continue;
                }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_FORMAT_ERROR)
                {
                    // MQGMO_CONVERT can't convert binary (MQFMT_NONE) data — proceed with unconverted payload
                    gotMessage = true;
                }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_Q_MGR_QUIESCING ||
                                              ex.ReasonCode == MQC.MQRC_CONNECTION_BROKEN)
                {
                    if (!ct.IsCancellationRequested)
                        _logger?.LogWarning("IBM MQ connection interrupted: RC={ReasonCode}", ex.ReasonCode);
                    break;
                }
                finally
                {
                    getSw.Stop();
                }

                // ─────────────────────────────────────────────────────────────
                // KNOWN ISSUE — IBM MQ managed .NET client (amqmdnetstd.dll)
                // ─────────────────────────────────────────────────────────────
                // The managed IBM MQ client is NOT event-driven on MQGET with
                // MQGMO_WAIT. It carries an internal polling tick of ~500 ms
                // that is INDEPENDENT of the WaitInterval supplied in MQGMO:
                // WaitInterval only governs the upper timeout, not the lower
                // delivery-granularity bound. As a result the typical
                // producer→consumer latency observed on this transport is
                // ~500 ms even with SHARECNV(1) on the channel.
                //
                // The native (unmanaged) client is event-driven but requires
                // the IBM MQ Client redistributable to be installed on the
                // host — not viable for self-contained .NET deployments.
                //
                // Proper fix: rewrite ReceiveLoopAsync to use the managed
                // async-consume API (MQQueue.Cb(...) + MQQueueManager.Ctl(
                // MQOP_START, ...)). With the callback path the broker pushes
                // messages to us and the per-message latency drops to ~0.
                // This is a non-trivial refactor (callback-driven instead of
                // poll-driven loop, different cancellation/back-pressure
                // model) — tracked for a future release.
                //
                // The Debug log below lets ops confirm the diagnosis in the
                // field: if "MQGET blocked for ~500 ms" appears consistently,
                // it is the managed-client tick (need MQCB); if values are
                // <50 ms while end-to-end latency is still ~500 ms, the
                // bottleneck is on the producer side instead.
                // ─────────────────────────────────────────────────────────────
                if (gotMessage && getSw.ElapsedMilliseconds > 50)
                {
                    _logger?.LogDebug(
                        "IBM MQ MQGET blocked for {ElapsedMs} ms before delivering message (destination={Destination})",
                        getSw.ElapsedMilliseconds, _endpoint.Destination);
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

        _logger?.LogDebug(
            "IBM MQ consumer: GOT message destination={Destination}, msgId={MsgId}, replyTo={ReplyTo}, msgType={MsgType}",
            _endpoint.Destination,
            IbmMqMessageHelper.BytesToHex(mqMsg.MessageId),
            mqMsg.ReplyToQueueName?.Trim(),
            mqMsg.MessageType);

        var exchange = CreateExchange(mqMsg);

        _logger?.LogDebug(
            "IBM MQ consumer: exchange CREATED, pattern={Pattern}, about to invoke route processor",
            exchange.Pattern);

        // Register transacted ack action
        if (_options.Transacted)
        {
            var qm = _qm ?? throw new InvalidOperationException("IBM MQ consumer not started");
            var ackAction = new IbmMqAckAction(qm, _logger);
            RegisterTransactedAction(exchange, $"ibmmq-ack-{Guid.NewGuid():N}", ackAction);
        }

        try
        {
            await _processor.Process(exchange, ct).ConfigureAwait(false);

            _logger?.LogDebug(
                "IBM MQ consumer: route processor RETURNED, hasOut={HasOut}, replyTo={ReplyTo}",
                exchange.HasOut, mqMsg.ReplyToQueueName?.Trim());

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
                    var qm = _qm ?? throw new InvalidOperationException("IBM MQ consumer not started");
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

            // Round-trip user headers via RFH2 (matches producer-side BuildOutgoingMessage)
            var headerSource = exchange.HasOut ? exchange.Out! : exchange.In;
            IbmMqMessageHelper.CopyHeadersToRfh2(reply, headerSource);

            var replyQueueName = originalMsg.ReplyToQueueName.Trim();
            var replyQmName = originalMsg.ReplyToQueueManagerName?.Trim();

            var qm = _qm ?? throw new InvalidOperationException("IBM MQ consumer not started");

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
            var reason = (ex as MQException)?.ReasonCode;
            _logger?.LogWarning(ex,
                "IBM MQ: failed to send RPC reply to {ReplyQueue} (reason={Reason}): {Message} — original message will still be settled",
                originalMsg.ReplyToQueueName?.Trim(), reason, ex.Message);
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
            var qm = _qm ?? throw new InvalidOperationException("IBM MQ consumer not started");
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
