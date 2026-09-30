using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.RabbitMQ;

/// <summary>
/// RabbitMQ consumer. Event-driven async consumer using <see cref="AsyncEventingBasicConsumer"/>.
/// Supports concurrent processing via semaphore, transacted channels (TxSelect, and a TxCommit after every ack or nack),
/// and automatic RPC reply when the incoming message has ReplyTo set.
/// </summary>
public sealed class RabbitMQConsumer : IConsumer
{
    private readonly RabbitMQEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private readonly RabbitMQEndpointOptions _options;
    private ILogger? _logger;
    private readonly SemaphoreSlim _semaphore;

    private IChannel? _channel;
    /// <summary>
    /// Dedicated channel for RPC reply publish. Created lazily on the first reply.
    /// Splitting consume/Ack from reply-publish prevents broker-side write backpressure on
    /// the reply path from blocking the Ack path — each channel has its own broker-side
    /// per-channel flow-control state, even though they share the underlying TCP connection.
    /// Channel-level recovery: if the broker closes <c>_replyChannel</c>, the next reply
    /// reopens it transparently.
    /// </summary>
    private IChannel? _replyChannel;
    private readonly SemaphoreSlim _replyChannelLock = new(1, 1);
    private AsyncEventingBasicConsumer? _consumer;
    private string? _consumerTag;
    private string? _actualQueueName;
    private readonly InflightDrainGuard _drain = new();
    // Set by Stop: from then on a cancelled subscription or a closed channel is ours, not news.
    private volatile bool _stopping;

    /// <summary>Number of messages successfully processed.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);
    private long _processedCount;

    /// <summary>Drain timeout for graceful stop (default 30s).</summary>
    internal TimeSpan DrainTimeout { get => _drain.DrainTimeout; set => _drain.DrainTimeout = value; }

    /// <summary>Known AMQP basic property names that should not leak into user headers.</summary>
    private static readonly HashSet<string> BasicPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ContentType", "ContentEncoding", "DeliveryMode", "Priority",
        "CorrelationId", "ReplyTo", "Expiration", "MessageId",
        "Timestamp", "Type", "UserId", "AppId", "ClusterId"
    };

    /// <summary>Creates a RabbitMQ consumer.</summary>
    public RabbitMQConsumer(RabbitMQEndpoint endpoint, IProcessor processor, RabbitMQEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = endpoint.Logger;
        _semaphore = new SemaphoreSlim(options.ResolvedConcurrentConsumers);
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        _stopping = false;
        _drain.Start(ct);
        // Consumer channel: no publisher confirms.
        // We only Ack/Nack (each followed by TxCommit when transacted) here; RPC replies go on a channel of their own.
        // Enabling publisher confirms tracking on this channel adds latency to every reply
        // and creates extra contention in the .NET client's confirm-tracking machinery
        // (each BasicPublishAsync awaits a broker confirm). For RPC replies we explicitly
        // do not need delivery guarantees — the client either receives the reply within its
        // own timeout, or treats the call as failed.
        // ConcurrentConsumers is the single knob for consumer-side parallelism: it sizes both the
        // AMQP consumer-dispatch concurrency of THIS channel (how many deliveries the client hands us
        // in parallel) and the _semaphore below (the app-level cap on concurrent Process calls).
        // Without the explicit dispatch value the channel would pin to 1 (ctor default) and every
        // message would be handled serially regardless of ConcurrentConsumers — the RabbitMQ.Client
        // 7.x trap this fixes.
        var dispatchConcurrency = (ushort)Math.Clamp(_options.ResolvedConcurrentConsumers, 1, ushort.MaxValue);
        _channel = await _endpoint.CreateChannelAsync(
            RabbitMQConnectionUse.Consume,
            publisherConfirms: false,
            consumerDispatchConcurrency: dispatchConcurrency,
            ct: ct).ConfigureAwait(false);

        try
        {
            // Topology declaration (exchange, queue, bindings)
            await _endpoint.DeclareTopologyAsync(_channel, ct).ConfigureAwait(false);

            // Resolve actual queue name (important for auto-generated queues)
            _actualQueueName = string.IsNullOrEmpty(_endpoint.QueueName)
                ? (await _channel.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true,
                    cancellationToken: ct).ConfigureAwait(false)).QueueName
                : _endpoint.QueueName;

            // QoS
            await _channel.BasicQosAsync(
                prefetchSize: 0,
                prefetchCount: _options.PrefetchCount,
                global: false,
                cancellationToken: ct).ConfigureAwait(false);

            // Transacted channel
            if (_options.Transacted == true)
            {
                await _channel.TxSelectAsync(ct).ConfigureAwait(false);
                _logger?.LogDebug("RabbitMQ consumer: transacted channel enabled");
            }

            // Consumer setup
            _consumer = new AsyncEventingBasicConsumer(_channel);
            _consumer.ReceivedAsync += OnMessageReceivedAsync;
            _consumer.UnregisteredAsync += OnConsumerUnregisteredAsync;
            _channel.ChannelShutdownAsync += OnConsumeChannelShutdownAsync;

            // autoAck:true settles every delivery at the broker on hand-off (at-most-once) — no manual
            // BasicAck/BasicNack, and a throw in Process does NOT requeue. autoAck:false (default) keeps
            // the post-process manual ack / nack-requeue path. ackMode=auto with Transacted is rejected in Validate().
            _consumerTag = await _channel.BasicConsumeAsync(
                queue: _actualQueueName,
                autoAck: _options.AckMode == AckMode.Auto,
                consumer: _consumer,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Channel was created but subsequent setup failed — release it (close + dispose + unregister
            // from the endpoint's tracking list) to prevent a leak.
            _logger?.LogError(ex, "RabbitMQ consumer Start failed: queue={Queue}, exchange={Exchange}", _actualQueueName, _options.Exchange);
            await _endpoint.ReleaseChannelAsync(_channel, ct).ConfigureAwait(false);
            _channel = null;
            _drain.Dispose();
            throw;
        }

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation(
            "RabbitMQ consumer started: queue={Queue}, exchange={Exchange}, routingKey={RoutingKey}, prefetch={Prefetch}, concurrent={Concurrent}",
            _actualQueueName, _options.Exchange, _options.RoutingKey, _options.PrefetchCount, _options.ResolvedConcurrentConsumers);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        _stopping = true;
        // Stop accepting new messages
        if (_consumer is not null && _consumerTag is not null && _channel is { IsOpen: true })
        {
            try
            {
                await _channel.BasicCancelAsync(_consumerTag, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error cancelling RabbitMQ consumer");
            }
        }

        // Drain in-flight
        await _drain.DrainAsync(ct, _logger, $"rabbitmq:{_actualQueueName}").ConfigureAwait(false);
        _drain.Dispose();
        _consumer = null;
        _consumerTag = null;

        // Release the main consume channel. The consumer OWNS this channel, so the consumer must free
        // it here — RabbitMQEndpoint.Stop() only runs on full context teardown, NOT on a per-route
        // Stop/Start, so leaving the channel to the endpoint leaks one open (cancelled, idle) channel
        // per Stop/Start cycle. ReleaseChannelAsync also unregisters it from the endpoint's list, so a
        // later endpoint.Stop() won't touch it (and double-close is guarded anyway).
        IChannel? consumeChannel = _channel;
        _channel = null;
        await _endpoint.ReleaseChannelAsync(consumeChannel, ct).ConfigureAwait(false);

        // Close the dedicated reply channel (if it was ever opened).
        IChannel? replyChannel;
        await _replyChannelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            replyChannel = _replyChannel;
            _replyChannel = null;
        }
        finally
        {
            _replyChannelLock.Release();
        }

        if (replyChannel is not null)
        {
            try { if (replyChannel.IsOpen) await replyChannel.CloseAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "RabbitMQ: error closing RPC reply channel"); }
            try { replyChannel.Dispose(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "RabbitMQ: error disposing RPC reply channel"); }
        }

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("RabbitMQ consumer stopped: queue={Queue}", _actualQueueName);
    }

    /// <summary>A cancellation this consumer asked for (a stop); any other one, a timeout inside the route included, is a failure.</summary>
    private static bool IsOurStop(Exception ex, CancellationToken token) => ex is OperationCanceledException && token.IsCancellationRequested;

    // ── Losing the subscription ──

    // AMQP channel-level errors (access refused, not found, resource locked, precondition failed): the broker closes the
    // channel and keeps the connection, so connection recovery never reopens it.
    private static bool IsChannelLevelError(ushort replyCode) => replyCode is 403 or 404 or 405 or 406;

    private Task OnConsumeChannelShutdownAsync(object sender, ShutdownEventArgs args)
    {
        if (_stopping || args.Initiator == ShutdownInitiator.Application)
            return Task.CompletedTask;

        if (IsChannelLevelError(args.ReplyCode))
            _logger?.LogError(
                "RabbitMQ consumer on queue {Queue} stopped receiving: its channel was closed ({Code} {Reason}) and nothing " +
                "reopens it. Unacknowledged deliveries return to the queue; restart the route to consume again.",
                _actualQueueName, args.ReplyCode, args.ReplyText);
        else
            _logger?.LogWarning(
                "RabbitMQ consumer on queue {Queue} lost its channel with the connection ({Code} {Reason}). With automatic " +
                "recovery (the default) the subscription comes back with the connection; with automaticRecovery=false, " +
                "restart the route.",
                _actualQueueName, args.ReplyCode, args.ReplyText);
        return Task.CompletedTask;
    }

    private Task OnConsumerUnregisteredAsync(object sender, ConsumerEventArgs args)
    {
        // The broker cancels a subscription whose queue was deleted; the consumer would otherwise just go quiet.
        if (!_stopping)
            _logger?.LogError(
                "RabbitMQ consumer on queue {Queue} was cancelled by the broker (the queue was deleted, or its node left): it " +
                "receives nothing more. Restart the route once the queue is back.",
                _actualQueueName);
        return Task.CompletedTask;
    }

    // ── Message handling ──

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        // Counted before the wait, so a delivery still waiting for a slot is in flight for Stop's drain too: it must
        // not start processing after the drain gave up and disposed the processing token.
        _drain.Increment();
        await _semaphore.WaitAsync().ConfigureAwait(false);
        Exchange? exchange = null;
        var channel = _channel;
        // In ackMode=auto the broker settled the delivery on hand-off: there is nothing to ack or nack.
        var settleRequired = _options.AckMode == AckMode.Manual;
        // One settle per delivery, and the first attempt counts even when it throws: a second ack or nack
        // of the same tag is rejected with PRECONDITION_FAILED (unknown delivery tag), which closes the whole channel.
        var settled = false;
        // This delivery's processing token, taken now: the drain disposes its source when a stop gives up waiting.
        var token = _drain.ProcessingToken;
        try
        {
            var body = ea.Body.ToArray();

            using var span = StartConsumerSpan(ea);

            exchange = CreateExchange(ea, body);

            if (channel is not { IsOpen: true })
            {
                _logger?.LogWarning("RabbitMQ: channel closed before message could be processed, deliveryTag={DeliveryTag}", ea.DeliveryTag);
                return;
            }

            var pipelineFailed = false;
            try
            {
                try
                {
                    await _processor.Process(exchange, token).ConfigureAwait(false);
                    exchange.ThrowIfUnhandledFailure();
                }
                catch
                {
                    pipelineFailed = true;
                    throw;
                }

                // RPC reply: if incoming message has ReplyTo, send response.
                // SendReplyAsync swallows its own errors so a dead reply queue does NOT
                // prevent us from acking the original message — we still need to advance
                // past poison/dead-reply messages.
                if (!string.IsNullOrEmpty(ea.BasicProperties.ReplyTo))
                {
                    await SendReplyAsync(exchange, ea.BasicProperties.ReplyTo, ea.BasicProperties.CorrelationId, span.Activity, token)
                        .ConfigureAwait(false);
                }

                // The acknowledgement belongs to this consumer, not to a route's .Transacted() block: the block owns
                // the database and the outgoing sends, and has committed them by the time Process returns, so the
                // ack comes last and only a unit of work that ended well is acknowledged.
                if (settleRequired)
                {
                    settled = true;
                    await SettleAsync(channel, ea.DeliveryTag, ack: true).ConfigureAwait(false);
                }

                Interlocked.Increment(ref _processedCount);
            }
            catch (Exception ex)
            {
                // The route, the RPC reply or the ack failed: the unit of work did not end well. Only a stop of ours
                // cancelling it is not a failure; a timeout inside the route (HttpClient: TaskCanceledException) is.
                if (!IsOurStop(ex, token))
                    span.Activity.RecordFailure(ex);

                // A pipeline failure is already counted by the core's StatisticsProcessor; an ack
                // failure AFTER a successful pipeline (channel died at settle) is invisible to the
                // core, so the transport records it (ревью дуги, M13).
                if (!pipelineFailed)
                    _endpoint.RecordError(ex);
                _logger?.LogError(ex, "RabbitMQ message processing error: deliveryTag={DeliveryTag}", ea.DeliveryTag);

                // Nack with requeue, unless the ack above was already attempted. In ackMode=auto the broker
                // took the delivery on hand-off (at-most-once): it cannot be requeued, the error above is the only signal.
                if (settleRequired && !settled)
                {
                    settled = true;
                    try
                    {
                        await SettleAsync(channel, ea.DeliveryTag, ack: false).ConfigureAwait(false);
                    }
                    catch (Exception nackEx) { _logger?.LogWarning(nackEx, "Error nacking RabbitMQ message"); }
                }
            }
        }
        catch (Exception ex)
        {
            // Transport-level failure (settle/channel trouble outside the pipeline) - invisible
            // to the core's statistics wrappers, so the endpoint records it itself. Pipeline
            // errors are counted by the core's StatisticsProcessor - not here, that would double.
            _endpoint.RecordError(ex);
            _logger?.LogError(ex, "Fatal error in RabbitMQ message handler");

            // Last-resort nack — only if neither path above attempted a settle. Skipped in ackMode=auto:
            // the broker already settled, there is no tag to nack.
            if (settleRequired && !settled && channel is { IsOpen: true })
            {
                settled = true;
                try
                {
                    await SettleAsync(channel, ea.DeliveryTag, ack: false).ConfigureAwait(false);
                }
                catch (Exception nackEx) { _logger?.LogWarning(nackEx, "RabbitMQ: last-resort nack failed, channel may be dead"); }
            }
        }
        finally
        {
            if (exchange is not null)
                await exchange.DisposeAsync().ConfigureAwait(false);
            _drain.Decrement();
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Acks the delivery, or nacks it with requeue. On a transacted channel an ack or nack takes effect only at
    /// <c>tx.commit</c>, so each is followed by a commit — the nack too: <c>tx.rollback</c> would discard it and leave
    /// the delivery unacknowledged until the channel closes (Spring AMQP commits a transactional reject the same way).
    /// Concurrent deliveries on the channel need no lock: a commit applies every settle pending on the channel, each
    /// of them already final, and the commit that follows each settle at worst finds the transaction empty.
    /// </summary>
    private async Task SettleAsync(IChannel channel, ulong deliveryTag, bool ack)
    {
        if (ack)
            await channel.BasicAckAsync(deliveryTag, multiple: false).ConfigureAwait(false);
        else
            await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true).ConfigureAwait(false);

        if (_options.Transacted == true)
            await channel.TxCommitAsync().ConfigureAwait(false);

        _logger?.LogDebug("RabbitMQ {Settle}: deliveryTag={DeliveryTag}", ack ? "ack" : "nack (requeue)", deliveryTag);
    }

    // ── Trace context propagation ──

    /// <summary>
    /// Opens the receive span through the core's transport contract: the parent comes from the message's W3C headers
    /// only (without them the span is a root, never a child of the dispatch thread's ambient activity), the sender's
    /// baggage is put back, the span names its endpoint, and <c>EnableTelemetry=false</c> opens nothing.
    /// </summary>
    private TransportSpan StartConsumerSpan(BasicDeliverEventArgs ea)
    {
        var span = RouteTelemetryExtensions.StartConsumerSpan(
            (_endpoint.Component as ComponentBase)?.Context,
            $"{ea.Exchange} receive", ActivityKind.Consumer, "messaging.system", "rabbitmq",
            _endpoint.Uri.NormalizedKey, ea.BasicProperties.Headers, ReadTraceHeader,
            destination: ea.Exchange, operation: "receive");

        if (span.Activity is { IsAllDataRequested: true } activity)
        {
            activity.SetTag("messaging.rabbitmq.destination.routing_key", ea.RoutingKey);
            activity.SetTag("messaging.message.delivery_tag", ea.DeliveryTag);
        }
        return span;
    }

    /// <summary>A trace header as AMQP carries it: RabbitMQ hands string headers over as UTF-8 bytes.</summary>
    private static string? ReadTraceHeader(IDictionary<string, object?>? headers, string name)
        => headers is not null && headers.TryGetValue(name, out var raw)
            ? raw switch
            {
                null => null,
                string s => s,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                _ => raw.ToString(),
            }
            : null;

    // ── Exchange creation ──

    private Exchange CreateExchange(BasicDeliverEventArgs ea, byte[] body)
    {
        var message = MessageFrom(ea, body);

        var pattern = string.IsNullOrEmpty(ea.BasicProperties.ReplyTo)
            ? ExchangePattern.InOnly
            : ExchangePattern.InOut;

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = pattern;
        return exchange;
    }

    /// <summary>
    /// A delivery as a message: the body as bytes, the AMQP headers, the <c>redbRmq.*</c> delivery metadata and the
    /// basic properties by name. One mapping for every inbound path — a consumed message and an RPC reply alike.
    /// </summary>
    internal static Message MessageFrom(BasicDeliverEventArgs ea, byte[] body)
    {
        var message = new Message(body);

        // Transfer AMQP headers
        if (ea.BasicProperties.Headers is not null)
        {
            foreach (var header in ea.BasicProperties.Headers)
            {
                object? value = header.Value switch
                {
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    string str => str,
                    _ => header.Value
                };

                if (value is not null)
                    message.Headers[header.Key] = value;
            }
        }

        // AMQP metadata as rabbitmq.* headers
        message.Headers[RmqHeaders.Exchange] = ea.Exchange;
        message.Headers[RmqHeaders.RoutingKey] = ea.RoutingKey;
        message.Headers[RmqHeaders.DeliveryTag] = ea.DeliveryTag;
        message.Headers[RmqHeaders.Redelivered] = ea.Redelivered;
        message.Headers[RmqHeaders.ConsumerTag] = ea.ConsumerTag;

        if (!string.IsNullOrEmpty(ea.BasicProperties.ContentType))
        {
            message.Headers[RmqHeaders.ContentType] = ea.BasicProperties.ContentType;
            message.ContentType = ea.BasicProperties.ContentType;
        }

        // Stamp AMQP basic properties into headers by name (the bare well-known names) via cached
        // reflection — symmetric with the producer's forwarding, so a consume→produce round-trip
        // carries them through. Simple string/byte properties go through the loop; Timestamp
        // (AmqpTimestamp) and persistence (DeliveryMode) are explicit — reflection can't map them.
        foreach (var prop in _stampableProps)
        {
            var val = prop.GetValue(ea.BasicProperties);
            if (val is string s)
            {
                if (!string.IsNullOrEmpty(s)) message.Headers[prop.Name] = s;
            }
            else if (val is byte b)
            {
                if (b > 0) message.Headers[prop.Name] = b;
            }
        }

        if (ea.BasicProperties.Timestamp.UnixTime != 0)
            message.Headers[RmqHeaders.Timestamp] = ea.BasicProperties.Timestamp.UnixTime;
        message.Headers["Persistent"] = ea.BasicProperties.Persistent;
        return message;
    }

    /// <summary>
    /// Readable string/byte <see cref="global::RabbitMQ.Client.IReadOnlyBasicProperties"/> stamped into
    /// headers by name via cached reflection. Excludes Headers (bulk-copied), ContentType (explicit — also
    /// sets Message.ContentType) and DeliveryMode (persistence handled explicitly as a bool).
    /// </summary>
    private static readonly System.Reflection.PropertyInfo[] _stampableProps = BuildStampableProps();

    private static System.Reflection.PropertyInfo[] BuildStampableProps()
    {
        var list = new System.Collections.Generic.List<System.Reflection.PropertyInfo>();
        foreach (var p in typeof(global::RabbitMQ.Client.IReadOnlyBasicProperties).GetProperties())
        {
            if (!p.CanRead) continue;
            if (p.Name is "Headers" or "ContentType" or "DeliveryMode") continue;
            if (p.PropertyType == typeof(string) || p.PropertyType == typeof(byte))
                list.Add(p);
        }
        return list.ToArray();
    }

    // ── RPC reply ──

    private async Task SendReplyAsync(IExchange exchange, string replyTo, string? correlationId, Activity? span, CancellationToken token)
    {
        IChannel? replyChannel;
        try
        {
            replyChannel = await GetOrCreateReplyChannelAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!IsOurStop(ex, token))
                span.RecordFailure(ex);
            _logger?.LogWarning(ex,
                "RabbitMQ: failed to open reply channel for {ReplyTo} — original message will still be acked", replyTo);
            return;
        }

        if (replyChannel is not { IsOpen: true })
        {
            _logger?.LogWarning("RabbitMQ: cannot send RPC reply — reply channel is closed. replyTo={ReplyTo}", replyTo);
            return;
        }

        try
        {
            var response = exchange.HasOut ? exchange.Out! : exchange.In;
            var responseObj = response.Body;

            var body = responseObj switch
            {
                byte[] bytes => bytes,
                string str   => Encoding.UTF8.GetBytes(str),
                null         => Array.Empty<byte>(),
                var other    => Encoding.UTF8.GetBytes(other.ToString() ?? string.Empty)
            };

            var properties = new BasicProperties
            {
                // The reply's own type, not the request's: a JSON request may well be answered with a PDF.
                ContentType = response.ContentType ?? _options.ContentType,
                Persistent = false
            };

            if (!string.IsNullOrEmpty(correlationId))
                properties.CorrelationId = correlationId;

            // Copy response headers (skip rabbitmq.* internal and AMQP basic property names)
            var headers = new Dictionary<string, object?>();

            foreach (var (key, value) in response.Headers)
            {
                if (RmqHeaders.IsRedbHeader(key)) continue;
                if (BasicPropertyNames.Contains(key)) continue;
                if (value is null) continue;

                headers[key] = value switch
                {
                    string s => s,
                    byte[] b => b,
                    _ => value.ToString()
                };
            }

            if (headers.Count > 0)
                properties.Headers = headers!;

            // mandatory:false — RPC reply queues are typically auto-generated, exclusive,
            // auto-delete (amq.gen-*). If the originating client has timed out and disconnected,
            // its reply queue is already gone. Setting mandatory:true would cause the broker
            // to send BasicReturn back, which on a confirm-tracking channel surfaces as a
            // PublishException — pure noise that would block useful work and inflate logs.
            // We deliberately fire-and-forget the reply: if the client is still listening,
            // it gets the reply; if not, we move on and ack the original request.
            await replyChannel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: replyTo,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: token).ConfigureAwait(false);

            _logger?.LogDebug("RabbitMQ RPC reply sent: replyTo={ReplyTo}, correlationId={CorrelationId}",
                replyTo, correlationId);
        }
        catch (Exception ex)
        {
            // Swallow: a failed reply must NOT prevent acking the original message.
            // The client will detect failure via its own RPC timeout.
            // It is still a failure of this delivery, so its span says so.
            if (!IsOurStop(ex, token))
                span.RecordFailure(ex);
            _logger?.LogWarning(ex, "RabbitMQ: failed to send RPC reply to {ReplyTo} (correlationId={CorrelationId}) — original message will still be acked",
                replyTo, correlationId);
        }
    }

    /// <summary>
    /// Returns the dedicated RPC-reply channel, opening it on first use and transparently
    /// reopening it if the broker has closed it since the last reply (e.g. after a network
    /// hiccup or channel-level exception). The reply channel always uses
    /// <c>publisherConfirms: false</c> — RPC reply is best-effort.
    /// </summary>
    private async Task<IChannel?> GetOrCreateReplyChannelAsync(CancellationToken ct)
    {
        var current = _replyChannel;
        if (current is { IsOpen: true }) return current;

        await _replyChannelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_replyChannel is { IsOpen: true }) return _replyChannel;

            // Drop the dead channel handle (will be closed by the connection's tracker on close).
            var dead = _replyChannel;
            _replyChannel = null;
            if (dead is not null)
            {
                try { dead.Dispose(); }
                catch (Exception ex) { _logger?.LogDebug(ex, "RabbitMQ: error disposing closed reply channel"); }
            }

            _replyChannel = await _endpoint.CreateChannelAsync(RabbitMQConnectionUse.Publish, publisherConfirms: false, ct: ct).ConfigureAwait(false);
            _logger?.LogDebug("RabbitMQ: dedicated RPC reply channel opened for queue {Queue}", _actualQueueName);
            return _replyChannel;
        }
        finally
        {
            _replyChannelLock.Release();
        }
    }

}
