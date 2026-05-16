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
/// Supports concurrent processing via semaphore, transacted channels (TxSelect/TxCommit/TxRollback),
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

    /// <summary>Number of messages successfully processed.</summary>
    public long ProcessedCount { get; private set; }

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
        _semaphore = new SemaphoreSlim(options.ConcurrentConsumers);
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        _drain.Start(ct);
        // Consumer channel: no publisher confirms.
        // We only Ack/Nack/TxCommit/TxRollback here, plus an optional best-effort RPC reply.
        // Enabling publisher confirms tracking on this channel adds latency to every reply
        // and creates extra contention in the .NET client's confirm-tracking machinery
        // (each BasicPublishAsync awaits a broker confirm). For RPC replies we explicitly
        // do not need delivery guarantees — the client either receives the reply within its
        // own timeout, or treats the call as failed.
        _channel = await _endpoint.CreateChannelAsync(
            publisherConfirms: false, ct: ct).ConfigureAwait(false);

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
            if (_options.Transacted)
            {
                await _channel.TxSelectAsync(ct).ConfigureAwait(false);
                _logger?.LogDebug("RabbitMQ consumer: transacted channel enabled");
            }

            // Consumer setup
            _consumer = new AsyncEventingBasicConsumer(_channel);
            _consumer.ReceivedAsync += OnMessageReceivedAsync;

            _consumerTag = await _channel.BasicConsumeAsync(
                queue: _actualQueueName,
                autoAck: false,
                consumer: _consumer,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Channel was created but subsequent setup failed — close it to prevent leak
            _logger?.LogError(ex, "RabbitMQ consumer Start failed: queue={Queue}, exchange={Exchange}", _actualQueueName, _options.Exchange);
            try { await _channel.CloseAsync(ct).ConfigureAwait(false); }
            catch (Exception closeEx) { _logger?.LogWarning(closeEx, "RabbitMQ: error closing channel during Start cleanup"); }
            _channel.Dispose();
            _channel = null;
            _drain.Dispose();
            throw;
        }

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation(
            "RabbitMQ consumer started: queue={Queue}, exchange={Exchange}, routingKey={RoutingKey}, prefetch={Prefetch}, concurrent={Concurrent}",
            _actualQueueName, _options.Exchange, _options.RoutingKey, _options.PrefetchCount, _options.ConcurrentConsumers);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
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

        // Close the dedicated reply channel (if it was ever opened). The main consume
        // channel is closed by the endpoint's channel registry on RabbitMQEndpoint.Stop().
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

    // ── Message handling ──

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        _drain.Increment();
        Exchange? exchange = null;
        var channel = _channel;
        // Tracks whether we've already settled this delivery (ack OR nack). Prevents
        // a "double nack" cascade across the inner/outer catch blocks, which the
        // RabbitMQ broker rejects with PRECONDITION_FAILED — unknown delivery tag,
        // and which then closes the entire channel.
        bool acked = false;
        try
        {
            var body = ea.Body.ToArray();

            using var activity = StartConsumerActivity(ea);

            exchange = CreateExchange(ea, body);

            // Deferred ack action for transacted mode
            if (channel is not { IsOpen: true })
            {
                _logger?.LogWarning("RabbitMQ: channel closed before message could be processed, deliveryTag={DeliveryTag}", ea.DeliveryTag);
                return;
            }

            var ackAction = new RabbitMQAckAction(channel, ea.DeliveryTag, _options.Transacted, _logger);
            RegisterTransactedAction(exchange, $"rabbitmq-ack-{ea.DeliveryTag}", ackAction);

            try
            {
                await _processor.Process(exchange, _drain.ProcessingToken).ConfigureAwait(false);

                // RPC reply: if incoming message has ReplyTo, send response.
                // SendReplyAsync swallows its own errors so a dead reply queue does NOT
                // prevent us from acking the original message — we still need to advance
                // past poison/dead-reply messages.
                if (!string.IsNullOrEmpty(ea.BasicProperties.ReplyTo))
                {
                    await SendReplyAsync(exchange, ea.BasicProperties.ReplyTo, ea.BasicProperties.CorrelationId)
                        .ConfigureAwait(false);
                }

                // Non-transacted: explicit ack
                if (!_options.Transacted)
                {
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false).ConfigureAwait(false);
                }
                else
                {
                    await channel.TxCommitAsync().ConfigureAwait(false);
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false).ConfigureAwait(false);
                }
                acked = true;

                ProcessedCount++;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "RabbitMQ message processing error: deliveryTag={DeliveryTag}", ea.DeliveryTag);

                if (_options.Transacted)
                {
                    try { await channel.TxRollbackAsync().ConfigureAwait(false); }
                    catch (Exception txEx) { _logger?.LogWarning(txEx, "Error rolling back RabbitMQ transaction"); }
                }

                // Nack with requeue — only if we haven't already settled this delivery
                if (!acked)
                {
                    try
                    {
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true).ConfigureAwait(false);
                        acked = true;
                    }
                    catch (Exception nackEx) { _logger?.LogWarning(nackEx, "Error nacking RabbitMQ message"); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Fatal error in RabbitMQ message handler");

            // Last-resort nack — only if neither path above settled the delivery.
            // Avoids the "unknown delivery tag" cascade when the inner catch already
            // nacked or when we successfully acked but then threw later.
            if (!acked && channel is { IsOpen: true })
            {
                try
                {
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true).ConfigureAwait(false);
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

    // ── Trace context propagation ──

    /// <summary>
    /// Extracts W3C trace context from AMQP headers and starts a Consumer activity
    /// linked to the producer's trace.
    /// </summary>
    private Activity? StartConsumerActivity(BasicDeliverEventArgs ea)
    {
        var propagator = DistributedContextPropagator.Current;

        propagator.ExtractTraceIdAndState(ea.BasicProperties.Headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not IDictionary<string, object?> h) return;
                if (!h.TryGetValue(key, out var raw) || raw is null) return;

                value = raw switch
                {
                    string s => s,
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    _ => raw.ToString()
                };
            },
            out var traceParent,
            out var traceState);

        ActivityContext parentContext = default;
        if (!string.IsNullOrEmpty(traceParent))
            ActivityContext.TryParse(traceParent, traceState, out parentContext);

        var activity = RouteActivitySource.Source.StartActivity(
            $"{ea.Exchange} receive",
            ActivityKind.Consumer,
            parentContext);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "rabbitmq");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", ea.Exchange);
            activity.SetTag("messaging.rabbitmq.destination.routing_key", ea.RoutingKey);
            activity.SetTag("messaging.message.delivery_tag", ea.DeliveryTag);
        }

        return activity;
    }

    // ── Exchange creation ──

    private Exchange CreateExchange(BasicDeliverEventArgs ea, byte[] body)
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
        if (!string.IsNullOrEmpty(ea.BasicProperties.CorrelationId))
            message.Headers[RmqHeaders.CorrelationId] = ea.BasicProperties.CorrelationId;
        if (!string.IsNullOrEmpty(ea.BasicProperties.ReplyTo))
            message.Headers[RmqHeaders.ReplyTo] = ea.BasicProperties.ReplyTo;
        if (!string.IsNullOrEmpty(ea.BasicProperties.MessageId))
            message.Headers[RmqHeaders.MessageId] = ea.BasicProperties.MessageId;

        var pattern = string.IsNullOrEmpty(ea.BasicProperties.ReplyTo)
            ? ExchangePattern.InOnly
            : ExchangePattern.InOut;

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = pattern;
        return exchange;
    }

    // ── RPC reply ──

    private async Task SendReplyAsync(IExchange exchange, string replyTo, string? correlationId)
    {
        IChannel? replyChannel;
        try
        {
            replyChannel = await GetOrCreateReplyChannelAsync(_drain.ProcessingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
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
            var responseObj = exchange.HasOut
                ? exchange.Out!.Body
                : exchange.In.Body;

            var body = responseObj switch
            {
                byte[] bytes => bytes,
                string str   => Encoding.UTF8.GetBytes(str),
                null         => Array.Empty<byte>(),
                var other    => Encoding.UTF8.GetBytes(other.ToString() ?? string.Empty)
            };

            var properties = new BasicProperties
            {
                ContentType = exchange.In.ContentType ?? _options.ContentType,
                Persistent = false
            };

            if (!string.IsNullOrEmpty(correlationId))
                properties.CorrelationId = correlationId;

            // Copy response headers (skip rabbitmq.* internal and AMQP basic property names)
            var responseHeaders = exchange.HasOut ? exchange.Out!.Headers : exchange.In.Headers;
            var headers = new Dictionary<string, object?>();

            foreach (var (key, value) in responseHeaders)
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
                cancellationToken: _drain.ProcessingToken).ConfigureAwait(false);

            _logger?.LogDebug("RabbitMQ RPC reply sent: replyTo={ReplyTo}, correlationId={CorrelationId}",
                replyTo, correlationId);
        }
        catch (Exception ex)
        {
            // Swallow: a failed reply must NOT prevent acking the original message.
            // The client will detect failure via its own RPC timeout.
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

            _replyChannel = await _endpoint.CreateChannelAsync(publisherConfirms: false, ct: ct).ConfigureAwait(false);
            _logger?.LogDebug("RabbitMQ: dedicated RPC reply channel opened for queue {Queue}", _actualQueueName);
            return _replyChannel;
        }
        finally
        {
            _replyChannelLock.Release();
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

/// <summary>
/// Deferred RabbitMQ acknowledgement action. Commits ack or rolls back with nack.
/// For transacted channels, uses TxCommit/TxRollback.
/// </summary>
internal sealed class RabbitMQAckAction : ITransactedAction
{
    private readonly IChannel _channel;
    private readonly ulong _deliveryTag;
    private readonly bool _transacted;
    private readonly ILogger? _logger;

    public RabbitMQAckAction(IChannel channel, ulong deliveryTag, bool transacted, ILogger? logger)
    {
        _channel = channel;
        _deliveryTag = deliveryTag;
        _transacted = transacted;
        _logger = logger;
    }

    public async Task Commit(CancellationToken ct = default)
    {
        if (_transacted)
        {
            await _channel.TxCommitAsync(ct).ConfigureAwait(false);
        }

        await _channel.BasicAckAsync(_deliveryTag, multiple: false, ct).ConfigureAwait(false);
        _logger?.LogDebug("RabbitMQ ack committed: deliveryTag={DeliveryTag}", _deliveryTag);
    }

    public async Task Rollback(CancellationToken ct = default)
    {
        if (_transacted)
        {
            await _channel.TxRollbackAsync(ct).ConfigureAwait(false);
        }

        await _channel.BasicNackAsync(_deliveryTag, multiple: false, requeue: true, ct).ConfigureAwait(false);
        _logger?.LogDebug("RabbitMQ nack (rollback): deliveryTag={DeliveryTag}", _deliveryTag);
    }
}
