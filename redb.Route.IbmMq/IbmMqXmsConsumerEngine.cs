using System.Collections.Concurrent;
using System.Diagnostics;
using IBM.XMS;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Transactions;
using RouteMessage = redb.Route.Core.Message;
using XmsMessage = IBM.XMS.IMessage;

namespace redb.Route.IbmMq;

/// <summary>
/// Event-driven IBM MQ receive engine built on XMS .NET (<c>IBM.XMS</c>).
/// <para>
/// Activated by <see cref="IbmMqEndpointOptions.ReceiveMode"/> = <see cref="IbmMqReceiveMode.Listener"/>.
/// Unlike the default poll loop (<see cref="IbmMqConsumer"/>, synchronous MQGET-WAIT on IBM.WMQ, which
/// carries the managed client's internal ~500&#160;ms tick), the broker <b>pushes</b> messages into an
/// XMS <c>MessageListener</c> the instant they arrive — dropping delivery latency to &lt;50&#160;ms.
/// </para>
/// <para>
/// <b>Threading / back-pressure (plan §5a).</b> XMS invokes the listener on a client-managed dispatch
/// thread, one message at a time per session. We run the whole route <b>synchronously inside the
/// callback</b>: a session will not deliver its next message until the current one returns, so each
/// session carries a single in-flight exchange. <c>ConcurrentConsumers=N</c> on a queue creates N
/// sessions on the shared connection (N competing consumers, N in-flight, queue-manager load-balanced);
/// a topic clamps to one subscriber (parallel subscriptions would duplicate delivery). XMS forbids
/// creating/closing a session inside the callback, so all session objects are built up-front in
/// <see cref="StartAsync"/> and torn down in <see cref="StopAsync"/>.
/// </para>
/// <para>
/// <b>Acknowledgement.</b> When <see cref="IbmMqEndpointOptions.Transacted"/> is false the session uses
/// <c>AutoAcknowledge</c>: the message is settled (destructively consumed) when the callback returns,
/// and processing errors are logged but swallowed — a failed route does not redeliver, mirroring the
/// non-transacted poll path. When <c>Transacted</c> is true the session is <c>SESSION_TRANSACTED</c>
/// and an <see cref="IbmMqXmsAckAction"/> is registered on the exchange: a route-level
/// <c>.Transaction()</c> block commits/rolls back the session as part of its unit-of-work exactly like
/// the poll path's <see cref="IbmMqAckAction"/>; if the route has no transaction block, the engine
/// settles the session itself (commit on success, rollback on error) after the route returns. The ack
/// action is idempotent, so the two paths never double-settle.
/// </para>
/// <para>
/// <b>Parity with the poll path.</b> The listener path also honours request-reply (a message whose
/// <c>JMSReplyTo</c> is set gets an <c>InOut</c> exchange and the <c>Out</c> body is sent back with the
/// request's message id as correlation id), the backout threshold (a message whose delivery count has
/// reached <c>BackoutThreshold</c> is copied to <c>BackoutQueue</c>), and W3C trace-context propagation
/// (<c>traceparent</c>/<c>tracestate</c> read from the message properties to continue the distributed
/// trace). RPC reply and the backout copy are issued on the <b>delivering session</b>, so under
/// <c>transacted</c> they commit atomically with consuming the request.
/// </para>
/// </summary>
internal sealed class IbmMqXmsConsumerEngine
{
    private readonly IbmMqEndpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly IbmMqEndpointOptions _options;
    private readonly ILogger? _logger;
    private readonly Action _onProcessed;
    private readonly InflightDrainGuard _drain = new();
    private readonly bool _transacted;

    private IConnection? _connection;
    private readonly List<XmsWorker> _workers = new();
    private volatile bool _stopping;

    /// <summary>
    /// One competing consumer: an XMS session (single-threaded — delivers one message at a time) plus its
    /// consumer. <c>ConcurrentConsumers=N</c> on a queue creates N of these on the shared connection, so N
    /// listeners run concurrently and the queue manager load-balances across them. The transacted syncpoint
    /// is per-session, so commit/rollback binds to the <see cref="Session"/> that delivered the message.
    /// </summary>
    private sealed class XmsWorker
    {
        public required ISession Session { get; init; }
        public required IMessageConsumer Consumer { get; init; }
    }

    public IbmMqXmsConsumerEngine(
        IbmMqEndpoint endpoint,
        IProcessor processor,
        IbmMqEndpointOptions options,
        ILogger? logger,
        Action onProcessed)
    {
        _endpoint = endpoint;
        _processor = processor;
        _options = options;
        _logger = logger;
        _onProcessed = onProcessed;
        _transacted = options.Transacted;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _drain.Start(ct);

        // XMS connection/session setup is synchronous and blocking (socket handshake). Push it to a
        // thread-pool worker so async callers don't block on it, mirroring IbmMqComponent's queue-manager
        // creation.
        await Task.Run(() =>
        {
            var factory = BuildConnectionFactory(_options);

            _connection = string.IsNullOrEmpty(_options.User)
                ? factory.CreateConnection()
                : factory.CreateConnection(_options.User, _options.Password);

            _connection.ExceptionListener = OnConnectionException;

            var isTopic = _options.DestinationType == IbmMqDestinationType.Topic;
            var workerCount = Math.Max(1, _options.ConcurrentConsumers);

            // Topics can't be load-balanced across competing subscribers: each non-durable subscription
            // gets its OWN copy of every message, so N subscribers would DUPLICATE delivery, not share it.
            // Clamp to one subscriber and tell the operator why — same rule as the poll path.
            if (isTopic && workerCount > 1)
            {
                _logger?.LogWarning(
                    "IBM MQ: ConcurrentConsumers({N}) ignored for TOPIC destination {Destination} — parallel " +
                    "subscribers would each receive a full copy of every message (duplicate delivery), not share " +
                    "the load. Running a single subscriber. Use a QUEUE destination for competing consumers.",
                    workerCount, _endpoint.Destination);
                workerCount = 1;
            }

            for (var i = 0; i < workerCount; i++)
            {
                // Transacted → SESSION_TRANSACTED (commit/rollback via IbmMqXmsAckAction); otherwise
                // AutoAcknowledge settles the message destructively when the listener returns. Each worker
                // owns its own session so the transacted syncpoint stays per-consumer.
                var session = _transacted
                    ? _connection.CreateSession(transacted: true, AcknowledgeMode.SessionTransacted)
                    : _connection.CreateSession(transacted: false, AcknowledgeMode.AutoAcknowledge);

                IDestination destination = isTopic
                    ? session.CreateTopic(_endpoint.Destination)
                    : session.CreateQueue(_endpoint.Destination);

                var consumer = string.IsNullOrEmpty(_options.Selector)
                    ? session.CreateConsumer(destination)
                    : session.CreateConsumer(destination, _options.Selector);

                // Capture THIS worker's session so commit/rollback binds to the session that delivered.
                consumer.MessageListener = m => OnMessage(m, session);

                _workers.Add(new XmsWorker { Session = session, Consumer = consumer });
            }

            // Nothing is delivered until the connection is started.
            _connection.Start();
        }, ct).ConfigureAwait(false);

        _logger?.LogInformation(
            "IBM MQ consumer started (XMS listener): destination={Destination}, type={Type}, concurrent={Concurrent}",
            _endpoint.Destination, _options.DestinationType, _workers.Count);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopping = true;

        // Stop delivery first so no new callbacks fire, then drain the in-flight one (the listener may
        // still be running the route synchronously), then close the XMS objects.
        try { _connection?.Stop(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ XMS: error stopping connection"); }

        await _drain.DrainAsync(ct, _logger, $"xms:{_endpoint.Destination}").ConfigureAwait(false);

        foreach (var w in _workers)
        {
            try { w.Consumer.Close(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ XMS: error closing consumer"); }
            try { w.Session.Close(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ XMS: error closing session"); }
        }
        _workers.Clear();

        try { _connection?.Close(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ XMS: error closing connection"); }

        _connection = null;
        _drain.Dispose();

        _logger?.LogInformation("IBM MQ consumer stopped (XMS listener): destination={Destination}", _endpoint.Destination);
    }

    // ── Listener callback (XMS dispatch thread) ──

    private void OnMessage(XmsMessage message, ISession session)
    {
        if (_stopping || _drain.ProcessingToken.IsCancellationRequested)
            return;

        _drain.Increment();

        var ct = _drain.ProcessingToken;

        // Continue the W3C distributed trace carried in the message properties.
        using var activity = StartConsumerActivity(message);

        var exchange = CreateExchange(message);

        // Transacted: bind commit/rollback to THIS delivering session. Registered on the exchange so a
        // route-level .Transaction() block settles it as part of the route unit-of-work (like the poll
        // path's IbmMqAckAction); the engine settles it below if no such block did.
        IbmMqXmsAckAction? ack = null;
        if (_transacted)
        {
            ack = new IbmMqXmsAckAction(session, _logger);
            RegisterTransactedAction(exchange, $"ibmmq-xms-ack-{Guid.NewGuid():N}", ack);
        }

        try
        {
            // Run the route to completion on the callback thread (plan §5a): this blocks the session's
            // delivery of the next message, giving one-in-flight back-pressure per session. Bridging the
            // async pipeline onto the synchronous XMS thread is intentional — there is no ambient
            // SynchronizationContext in this server-side library, so GetResult() will not deadlock.
            _processor.Process(exchange, ct).GetAwaiter().GetResult();

            // Request-reply: send the Out body back to JMSReplyTo. On the delivering session, so under
            // transacted it commits atomically with consuming the request.
            SendReply(session, message, exchange);

            // Poison-message diversion: mirror the poll path — a message whose delivery count has reached
            // the threshold is copied to the backout queue.
            if (_options.BackoutThreshold > 0)
                MoveToBackoutQueueIfExceeded(session, message);

            // Commit the delivering session (no-op if a route .Transaction() block already committed it).
            ack?.Commit(ct).GetAwaiter().GetResult();

            _onProcessed();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "IBM MQ XMS message processing error: destination={Destination}", _endpoint.Destination);

            // Transacted: roll back so the message is redelivered / backout-counted (no-op if a route
            // .Transaction() block already rolled back). Non-transacted (ack == null): swallow — auto-ack
            // has already settled the message destructively, matching the non-transacted poll path.
            if (ack != null)
            {
                try { ack.Rollback(ct).GetAwaiter().GetResult(); }
                catch (Exception rollbackEx) { _logger?.LogWarning(rollbackEx, "IBM MQ XMS: session rollback failed"); }
            }
        }
        finally
        {
            try { exchange.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception disposeEx) { _logger?.LogDebug(disposeEx, "IBM MQ XMS: exchange dispose faulted"); }
            _drain.Decrement();
        }
    }

    /// <summary>Registers a deferred transacted action on the exchange (same registry the poll path uses).</summary>
    private static void RegisterTransactedAction(IExchange exchange, string key, ITransactedAction action)
    {
        if (!exchange.Properties.TryGetValue(TransactedProcessor.TransactActionPropertyKey, out var raw) ||
            raw is not ConcurrentDictionary<string, ITransactedAction> dict)
        {
            dict = new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase);
            exchange.Properties[TransactedProcessor.TransactActionPropertyKey] = dict;
        }

        dict[key] = action;
    }

    // ── XMS message → redb exchange ──

    private Exchange CreateExchange(XmsMessage message)
    {
        object body;
        string? contentType;

        switch (message)
        {
            case ITextMessage text:
                body = text.Text ?? string.Empty;
                contentType = "text/plain";
                break;
            case IBytesMessage bytes:
                var length = (int)bytes.BodyLength;
                var buffer = new byte[length];
                if (length > 0) bytes.ReadBytes(buffer);
                body = buffer;
                contentType = "application/octet-stream";
                break;
            default:
                body = string.Empty;
                contentType = null;
                break;
        }

        var routeMsg = new RouteMessage(body) { ContentType = contentType };

        // Carry the same redbIbmMq.* metadata and user headers as the poll path.
        IbmMqXmsHeaderMapper.CopyToHeaders(message, routeMsg, _endpoint.Destination, _options);

        var exchange = Exchange.Create(routeMsg, _endpoint.ScopeFactory);
        // A request carrying a reply destination is request-reply (InOut); otherwise one-way (InOnly).
        exchange.Pattern = HasReplyTo(message) ? ExchangePattern.InOut : ExchangePattern.InOnly;
        return exchange;
    }

    // ── W3C trace context propagation ──

    private Activity? StartConsumerActivity(XmsMessage message)
    {
        var traceParent = TryGetStringProperty(message, "traceparent");
        var traceState = TryGetStringProperty(message, "tracestate");

        ActivityContext parentContext = default;
        if (!string.IsNullOrEmpty(traceParent))
            ActivityContext.TryParse(traceParent, traceState, out parentContext);

        var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.Destination} receive", ActivityKind.Consumer, parentContext);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "wmq");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", _endpoint.Destination);
            activity.SetTag("messaging.ibmmq.queue_manager", _options.QueueManager);
        }

        return activity;
    }

    // ── RPC reply ──

    private void SendReply(ISession session, XmsMessage request, IExchange exchange)
    {
        IDestination? replyTo;
        try { replyTo = request.JMSReplyTo; } catch { return; }
        if (replyTo is null) return;

        try
        {
            var responseBody = exchange.HasOut ? exchange.Out!.Body : exchange.In.Body;

            XmsMessage reply;
            if (responseBody is byte[] bytes)
            {
                var bm = session.CreateBytesMessage();
                bm.WriteBytes(bytes);
                reply = bm;
            }
            else
            {
                reply = session.CreateTextMessage(responseBody?.ToString() ?? string.Empty);
            }

            // Standard IBM MQ correlation: reply MQMD CorrelId = request MQMD MsgId (as bytes). Setting it
            // via JMSCorrelationIDAsBytes lands it in the MQMD field the (non-JMS) MQ requester matches on;
            // a plain string JMSCorrelationID would go into an RFH2 folder the requester never reads.
            var msgIdBytes = JmsMessageIdToBytes(request.JMSMessageID);
            if (msgIdBytes is not null)
                try { reply.JMSCorrelationIDAsBytes = msgIdBytes; } catch { /* best-effort */ }
            else
                try { reply.JMSCorrelationID = request.JMSMessageID; } catch { /* best-effort */ }

            // Send to a FRESH queue destination built from the reply queue's name rather than the
            // JMSReplyTo object parsed off the received message: XMS treats the parsed destination as a
            // temporary destination owned by the requester's connection and refuses to send to it from
            // ours (IllegalStateException). A plain queue destination by name is an ordinary send.
            var replyDest = session.CreateQueue(replyTo.Name);

            var producer = session.CreateProducer(replyDest);
            // Reply non-persistent: RPC reply queues are typically temporary dynamic queues, which reject
            // persistent messages (MQRC_PERSISTENT_NOT_ALLOWED). XMS defaults a producer to Persistent
            // (the JMS default), unlike the poll path which inherits the queue's own default persistence.
            producer.DeliveryMode = DeliveryMode.NonPersistent;
            try { producer.Send(reply); }
            finally { try { producer.Close(); } catch { /* ignore */ } }

            _logger?.LogDebug("IBM MQ XMS RPC reply sent to {ReplyTo}", replyTo.Name);
        }
        catch (Exception ex)
        {
            // A failed reply must not block the consumer slot — the client detects it via its RPC timeout.
            _logger?.LogWarning(ex, "IBM MQ XMS: failed to send RPC reply — request will still be settled");
        }
    }

    // ── Backout queue ──

    private void MoveToBackoutQueueIfExceeded(ISession session, XmsMessage message)
    {
        int deliveryCount;
        try { deliveryCount = message.GetIntProperty(XMSC.JMSX_DELIVERY_COUNT); }
        catch { return; } // property not available — nothing to decide on

        // JMSXDeliveryCount is 1 on first delivery; backout count is deliveries minus the current one.
        var backoutCount = deliveryCount - 1;
        if (backoutCount < _options.BackoutThreshold) return;

        var boqName = _options.BackoutQueue;
        if (string.IsNullOrWhiteSpace(boqName))
        {
            _logger?.LogWarning("IBM MQ XMS: message exceeded backout threshold but no backout queue configured");
            return;
        }

        try
        {
            var boq = session.CreateQueue(boqName);
            var producer = session.CreateProducer(boq);
            try { producer.Send(message); }
            finally { try { producer.Close(); } catch { /* ignore */ } }

            _logger?.LogInformation(
                "IBM MQ XMS: poison message moved to backout queue {BackoutQueue} (destination={Destination}, backoutCount={BackoutCount})",
                boqName, _endpoint.Destination, backoutCount);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "IBM MQ XMS: failed to move message to backout queue {BackoutQueue}", boqName);
        }
    }

    private static bool HasReplyTo(XmsMessage message)
    {
        try { return message.JMSReplyTo is not null; }
        catch { return false; }
    }

    /// <summary>Parses a JMS message id ("ID:" + hex) into the raw MQMD message-id bytes, or null.</summary>
    private static byte[]? JmsMessageIdToBytes(string? jmsMessageId)
    {
        if (string.IsNullOrEmpty(jmsMessageId)) return null;
        var hex = jmsMessageId.StartsWith("ID:", StringComparison.Ordinal) ? jmsMessageId[3..] : jmsMessageId;
        if (hex.Length == 0 || hex.Length % 2 != 0) return null;
        try { return Convert.FromHexString(hex); }
        catch { return null; }
    }

    private static string? TryGetStringProperty(XmsMessage message, string name)
    {
        try { return message.PropertyExists(name) ? message.GetStringProperty(name) : null; }
        catch { return null; }
    }

    private void OnConnectionException(Exception ex)
    {
        if (!_stopping)
            _logger?.LogWarning(ex, "IBM MQ XMS connection error: destination={Destination}", _endpoint.Destination);
    }

    // ── Connection factory ──

    private static IConnectionFactory BuildConnectionFactory(IbmMqEndpointOptions options)
    {
        var factory = XMSFactoryFactory.GetInstance(XMSC.CT_WMQ).CreateConnectionFactory();

        factory.SetStringProperty(XMSC.WMQ_HOST_NAME, options.Host);
        factory.SetIntProperty(XMSC.WMQ_PORT, options.Port);
        factory.SetStringProperty(XMSC.WMQ_CHANNEL, options.Channel);
        factory.SetStringProperty(XMSC.WMQ_QUEUE_MANAGER, options.QueueManager);
        factory.SetIntProperty(XMSC.WMQ_CONNECTION_MODE, XMSC.WMQ_CM_CLIENT);

        if (!string.IsNullOrEmpty(options.ClientId))
            factory.SetStringProperty(XMSC.CLIENT_ID, options.ClientId);

        if (!string.IsNullOrEmpty(options.SslCipherSpec))
            factory.SetStringProperty(XMSC.WMQ_SSL_CIPHER_SPEC, options.SslCipherSpec);

        return factory;
    }
}
