using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using redb.Route.Abstractions;
using redb.Route.Transactions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.RabbitMQ;

/// <summary>
/// RabbitMQ producer. Sends messages to an exchange/queue with support for:
/// <list type="bullet">
///   <item><b>Immediate</b> — direct publish via <c>BasicPublishAsync</c>.</item>
///   <item><b>Transactional</b> — deferred publish via <see cref="ITransactedAction"/>. The message is
///     captured (cloned) at Process time and only sent on Commit.</item>
///   <item><b>RPC</b> — request/reply pattern with a temporary reply queue and
///     correlation-based response routing.</item>
/// </list>
/// </summary>
public sealed class RabbitMQProducer : ConnectableProducer
{
    private readonly RabbitMQEndpoint _endpoint;
    private readonly RabbitMQEndpointOptions _options;

    private IChannel? _channel;

    // ── RPC infrastructure ──
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IMessage>> _pendingResponses = new();
    private string? _replyQueueName;
    private AsyncEventingBasicConsumer? _responseConsumer;
    private string? _responseConsumerTag;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private volatile bool _rpcSetup;

    /// <summary>
    /// Serialises <see cref="IChannel.BasicPublishAsync(string, string, bool, CancellationToken)"/>
    /// calls on the producer's single channel. RabbitMQ.Client 7.x is documented as NOT thread-safe
    /// when multiple concurrent BasicPublishAsync calls run on the same channel with publisher
    /// confirm tracking enabled — the in-flight delivery-tag bookkeeping races and produces
    /// PRECONDITION_FAILED on confirm callbacks.
    /// <para>The lock is per-Producer (each Producer instance owns its own channel), so two routes
    /// with their own producers publish in parallel; only the same Producer's calls are serialised.</para>
    /// </summary>
    private readonly SemaphoreSlim _publishLock = new(1, 1);

    // The deferred publishes of a block go out as one batch per producer, on a channel of its own in tx mode (a channel
    // with publisher confirms cannot run transactions); the batches take turns on it.
    private readonly string _batchKey = $"rabbitmq-batch-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _commitLock = new(1, 1);
    private IChannel? _txChannel;

    /// <summary>Known AMQP basic property names that should not be propagated as custom headers.</summary>
    private static readonly HashSet<string> BasicPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ContentType", "ContentEncoding", "DeliveryMode", "Persistent", "Priority",
        "CorrelationId", "ReplyTo", "Expiration", "MessageId",
        "Timestamp", "Type", "UserId", "AppId", "ClusterId"
    };

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"rabbitmq:{_options.Exchange}/{_options.RoutingKey}";

    /// <summary>Creates a RabbitMQ producer.</summary>
    public RabbitMQProducer(RabbitMQEndpoint endpoint, RabbitMQEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // A request-reply send cannot wait for the commit: the reply it waits for would never come.
        if (_options.ReplyTo && _options.Transacted == true)
            throw new InvalidOperationException(
                $"'{ProducerName}' is a request-reply producer (replyTo=true): the request has to leave at once for its " +
                "reply to arrive, so it cannot be deferred to the commit of a transaction. Drop transacted=true: a " +
                "request-reply send always goes out at once, outside the transaction.");
    }

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _channel = await _endpoint.CreateChannelAsync(ct: ct).ConfigureAwait(false);

        try
        {
            // Declare topology if requested
            await _endpoint.DeclareTopologyAsync(_channel, ct).ConfigureAwait(false);

            // RPC: set up reply queue eagerly
            if (_options.ReplyTo)
                await SetupResponseQueueAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "RabbitMQ producer ConnectAsync failed for {Exchange}/{RoutingKey}",
                _options.Exchange, _options.RoutingKey);
            try { await _channel.CloseAsync(ct).ConfigureAwait(false); }
            catch (Exception closeEx) { Logger?.LogWarning(closeEx, "RabbitMQ: error closing channel during ConnectAsync cleanup"); }
            _channel.Dispose();
            _channel = null;
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task DisconnectAsync(CancellationToken ct)
    {
        // Cancel pending RPC responses
        foreach (var pending in _pendingResponses)
        {
            pending.Value.TrySetCanceled(ct);
        }
        _pendingResponses.Clear();

        // Shut down response consumer
        if (_responseConsumer is not null && _responseConsumerTag is not null && _channel is { IsOpen: true })
        {
            try { await _channel.BasicCancelAsync(_responseConsumerTag, cancellationToken: ct).ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogDebug(ex, "RabbitMQ: error cancelling response consumer during stop"); }
        }

        _responseConsumer = null;
        _responseConsumerTag = null;
        _rpcSetup = false;

        if (_txChannel is not null)
        {
            try { await _txChannel.CloseAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogDebug(ex, "RabbitMQ: error closing the transacted channel during stop"); }
            _txChannel.Dispose();
            _txChannel = null;
        }
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        EnsureChannel();

        using var activity = RouteActivitySource.Source.StartActivity(
            $"{_options.Exchange} publish", ActivityKind.Producer);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "rabbitmq");
            activity.SetTag("messaging.operation", "publish");
            activity.SetTag("messaging.destination.name",
                _options.ResolveOption(_options.Exchange, exchange) ?? _options.Exchange);
            activity.SetTag("messaging.rabbitmq.destination.routing_key",
                _options.ResolveOption(_options.RoutingKey, exchange) ?? _options.RoutingKey);
        }

        var (properties, body) = PrepareMessage(exchange);

        // Inject W3C trace context into AMQP headers
        InjectTraceContext(activity, properties);

        if (_options.ReplyTo)
        {
            await ProcessRpcAsync(exchange, properties, body, ct).ConfigureAwait(false);
        }
        else if (TransactedActions.Defers(exchange, _options.Transacted))
        {
            ProcessTransactional(exchange, properties, body);
        }
        else
        {
            await ProcessImmediateAsync(exchange, properties, body, ct).ConfigureAwait(false);
        }
    }

    // ── Immediate send ──

    private async Task ProcessImmediateAsync(IExchange exchange, BasicProperties properties, byte[] body, CancellationToken ct)
    {
        var resolvedExchange = _options.ResolveOption(_options.Exchange, exchange) ?? _options.Exchange;
        var resolvedRoutingKey = _options.ResolveOption(_options.RoutingKey, exchange) ?? _options.RoutingKey;

        try
        {
            await _publishLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _channel!.BasicPublishAsync(
                    exchange: resolvedExchange,
                    routingKey: resolvedRoutingKey,
                    mandatory: _options.ResolveMandatory(),
                    basicProperties: properties,
                    body: body,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            finally { _publishLock.Release(); }

            // Wire-level bytes are the connector's to record: the core's ToProcessor counts
            // MessagesOut/Errors for a routed producer but has no idea of payload sizes.
            _endpoint.RecordBytesOut(body.Length);

            Logger?.LogDebug("RabbitMQ immediate publish: exchange={Exchange}, routingKey={RoutingKey}, bodySize={Size}",
                resolvedExchange, resolvedRoutingKey, body.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPublishDiagnostics("Immediate", ex, _channel, resolvedExchange, resolvedRoutingKey,
                _options.ResolveMandatory(), body.Length, properties?.ContentType);
            throw;
        }
    }

    // ── Transactional deferred send ──

    private void ProcessTransactional(IExchange exchange, BasicProperties properties, byte[] body)
    {
        // Clone everything — the action might execute long after Process returns
        var capturedExchange = _options.ResolveOption(_options.Exchange, exchange) ?? _options.Exchange;
        var capturedRoutingKey = _options.ResolveOption(_options.RoutingKey, exchange) ?? _options.RoutingKey;
        var capturedMandatory = _options.ResolveMandatory();
        var capturedBody = new byte[body.Length];
        Buffer.BlockCopy(body, 0, capturedBody, 0, body.Length);
        var capturedProperties = CloneProperties(properties);

        var batch = TransactedActions.JoinBatch(exchange, _batchKey,
            () => new RabbitMQSendBatch(TransactedChannelAsync, _commitLock, Logger), ProducerName);
        batch.Add(new RabbitMQDeferredPublish(
            capturedExchange, capturedRoutingKey, capturedMandatory, capturedProperties, capturedBody));
    }

    /// <summary>
    /// The producer's channel in tx mode, opened for the first batch and again after a failed batch closed it. A batch
    /// calls it under the commit lock.
    /// </summary>
    private async Task<IChannel> TransactedChannelAsync(CancellationToken ct)
    {
        if (_txChannel is { IsOpen: true } open)
            return open;

        _txChannel?.Dispose();
        var channel = await _endpoint.CreateChannelAsync(publisherConfirms: false, ct: ct).ConfigureAwait(false);
        await channel.TxSelectAsync(ct).ConfigureAwait(false);
        _txChannel = channel;
        return channel;
    }

    // ── RPC (request/reply) ──

    private async Task ProcessRpcAsync(IExchange exchange, BasicProperties properties, byte[] body, CancellationToken ct)
    {
        if (!_rpcSetup)
            await SetupResponseQueueAsync(ct).ConfigureAwait(false);

        // First attempt; if the publish fails because the underlying channel/reply-queue is gone
        // (cluster failover, broker-side queue eviction, channel exception), recreate the reply
        // queue once and retry. Without this retry, every RPC after a node loss would hang for
        // the full RPC timeout (default 60s) before the caller learns of the failure.
        try
        {
            await PublishRpcOnceAsync(exchange, properties, body, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (IsRecoverableRpcFailure(ex))
        {
            Logger?.LogWarning(ex,
                "RabbitMQ RPC publish failed on first attempt \u2014 recreating reply queue and retrying");

            await RecreateResponseQueueAsync(ct).ConfigureAwait(false);

            // Properties.ReplyTo was set to the OLD queue inside PublishRpcOnceAsync; clear so
            // the second attempt picks up the NEW _replyQueueName.
            properties.ReplyTo = null;
            await PublishRpcOnceAsync(exchange, properties, body, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Heuristic: was the publish failure caused by the broker dropping our channel or our
    /// auto-deleted reply queue? If so, a single retry against a freshly recreated reply queue
    /// is the correct enterprise-grade response (failover-style).
    /// </summary>
    private static bool IsRecoverableRpcFailure(Exception ex) =>
        ex is global::RabbitMQ.Client.Exceptions.AlreadyClosedException
           or global::RabbitMQ.Client.Exceptions.OperationInterruptedException
           or global::RabbitMQ.Client.Exceptions.BrokerUnreachableException
           or global::RabbitMQ.Client.Exceptions.ChannelAllocationException
           or global::RabbitMQ.Client.Exceptions.PublishException;

    private async Task PublishRpcOnceAsync(IExchange exchange, BasicProperties properties, byte[] body, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        properties.CorrelationId = correlationId;
        properties.ReplyTo = _replyQueueName;

        var tcs = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[correlationId] = tcs;

        try
        {
            await _publishLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _channel!.BasicPublishAsync(
                    exchange: _options.ResolveOption(_options.Exchange, exchange) ?? _options.Exchange,
                    routingKey: _options.ResolveOption(_options.RoutingKey, exchange) ?? _options.RoutingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            finally { _publishLock.Release(); }

            Logger?.LogDebug("RabbitMQ RPC request sent: correlationId={CorrelationId}, replyTo={ReplyTo}",
                correlationId, _replyQueueName);

            // Wait for response with timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.Timeout));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var responseTask = tcs.Task;
            var completed = await Task.WhenAny(responseTask, Task.Delay(Timeout.Infinite, linked.Token))
                .ConfigureAwait(false);

            if (completed != responseTask)
            {
                _pendingResponses.TryRemove(correlationId, out _);
                throw new TimeoutException(
                    $"RabbitMQ RPC timeout ({_options.Timeout}s): exchange={_options.Exchange}, routingKey={_options.RoutingKey}");
            }

            var response = await responseTask.ConfigureAwait(false);

            // Set Out message
            exchange.Out = response;
            exchange.Pattern = ExchangePattern.InOut;

            Logger?.LogDebug("RabbitMQ RPC response received: correlationId={CorrelationId}", correlationId);
        }
        catch (Exception ex)
        {
            _pendingResponses.TryRemove(correlationId, out _);
            if (ex is not OperationCanceledException and not TimeoutException)
            {
                var resolvedExchange = _options.ResolveOption(_options.Exchange, exchange) ?? _options.Exchange;
                var resolvedRoutingKey = _options.ResolveOption(_options.RoutingKey, exchange) ?? _options.RoutingKey;
                LogPublishDiagnostics("RPC", ex, _channel, resolvedExchange, resolvedRoutingKey,
                    true, body.Length, properties?.ContentType,
                    correlationId, _replyQueueName, _options.Timeout);
            }
            throw;
        }
    }

    // ── RPC response queue setup ──

    private async Task SetupResponseQueueAsync(CancellationToken ct)
    {
        if (_rpcSetup) return;

        await _rpcLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rpcSetup) return;

            await DeclareReplyQueueAndConsumerAsync(ct).ConfigureAwait(false);
            _rpcSetup = true;
            Logger?.LogDebug("RabbitMQ RPC reply queue created: {Queue}", _replyQueueName);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    /// <summary>
    /// Tears down the previous reply consumer (if any) and re-declares a fresh exclusive
    /// auto-delete reply queue + consumer on the producer's channel. Used by
    /// <see cref="ProcessRpcAsync"/> to recover after a cluster failover or broker-side
    /// queue eviction. Pending RPC TCSes for the old queue are failed so callers see the error
    /// immediately instead of waiting for the full RPC timeout.
    /// </summary>
    private async Task RecreateResponseQueueAsync(CancellationToken ct)
    {
        await _rpcLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Cancel the old consumer; ignore errors — the channel is likely already dead.
            if (_responseConsumerTag is not null && _channel is { IsOpen: true })
            {
                try { await _channel.BasicCancelAsync(_responseConsumerTag, cancellationToken: ct).ConfigureAwait(false); }
                catch (Exception ex) { Logger?.LogDebug(ex, "RabbitMQ: error cancelling old reply consumer during recreate"); }
            }

            // Fail every in-flight RPC tied to the old reply queue — caller learns now, not after timeout.
            var oldQueue = _replyQueueName;
            foreach (var pending in _pendingResponses)
            {
                pending.Value.TrySetException(new InvalidOperationException(
                    $"RabbitMQ RPC reply queue '{oldQueue}' was lost (cluster failover); request must be retried"));
            }
            _pendingResponses.Clear();

            _responseConsumer = null;
            _responseConsumerTag = null;
            _rpcSetup = false;
            _replyQueueName = null;

            // The publisher channel itself may be closed too — recreate it through the endpoint
            // (which goes through the connection pool, transparently reconnecting if needed).
            if (_channel is null || !_channel.IsOpen)
            {
                try { _channel?.Dispose(); }
                catch (Exception ex) { Logger?.LogDebug(ex, "RabbitMQ: error disposing dead producer channel during recreate"); }

                _channel = await _endpoint.CreateChannelAsync(ct: ct).ConfigureAwait(false);
                Logger?.LogInformation("RabbitMQ producer channel recreated after RPC failure");
            }

            await DeclareReplyQueueAndConsumerAsync(ct).ConfigureAwait(false);
            _rpcSetup = true;
            Logger?.LogInformation("RabbitMQ RPC reply queue recreated: {Queue} (was {OldQueue})", _replyQueueName, oldQueue);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    /// <summary>
    /// Declares an exclusive auto-delete reply queue on the producer channel, sets QoS, and
    /// installs the response consumer. Caller must hold <see cref="_rpcLock"/>.
    /// </summary>
    private async Task DeclareReplyQueueAndConsumerAsync(CancellationToken ct)
    {
        var result = await _channel!.QueueDeclareAsync(
            queue: string.Empty,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: ct).ConfigureAwait(false);

        _replyQueueName = result.QueueName;

        await _channel.BasicQosAsync(0, (ushort)(_options.ResolvedConcurrentConsumers * 3), false, ct)
            .ConfigureAwait(false);

        _responseConsumer = new AsyncEventingBasicConsumer(_channel);
        _responseConsumer.ReceivedAsync += OnRpcResponseReceivedAsync;

        _responseConsumerTag = await _channel.BasicConsumeAsync(
            queue: _replyQueueName,
            autoAck: false,
            consumer: _responseConsumer,
            cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task OnRpcResponseReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var correlationId = ea.BasicProperties.CorrelationId;

            if (!string.IsNullOrEmpty(correlationId) && _pendingResponses.TryRemove(correlationId, out var tcs))
            {
                try
                {
                    var body = ea.Body.ToArray();
                    var message = new Message(Encoding.UTF8.GetString(body));

                    // Copy response headers
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

                    tcs.TrySetResult(message);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error processing RPC response");
            try { await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false).ConfigureAwait(false); }
            catch (Exception nackEx) { Logger?.LogWarning(nackEx, "RabbitMQ: error nacking RPC response"); }
        }
    }

    /// <summary>
    /// Injects W3C trace context (traceparent/tracestate) into AMQP BasicProperties headers
    /// using the .NET built-in DistributedContextPropagator.
    /// </summary>
    private static void InjectTraceContext(Activity? activity, BasicProperties properties)
    {
        if (activity is null) return;

        properties.Headers ??= new Dictionary<string, object?>();

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, properties.Headers, static (carrier, key, value) =>
        {
            if (carrier is IDictionary<string, object?> h && !string.IsNullOrEmpty(value))
            {
                h[key] = value;
            }
        });
    }

    // ── Message preparation ──

    private (BasicProperties Properties, byte[] Body) PrepareMessage(IExchange exchange)
    {
        var body = exchange.In.Body switch
        {
            byte[] bytes => bytes,
            string str   => Encoding.UTF8.GetBytes(str),
            null         => Array.Empty<byte>(),
            var other    => Encoding.UTF8.GetBytes(other.ToString() ?? string.Empty)
        };

        var properties = new BasicProperties
        {
            ContentType = exchange.In.ContentType ?? _options.ContentType, 
            Persistent = true
        };

        // Copy message headers → AMQP headers (skip rabbitmq.* and basic property names)
        var headers = new Dictionary<string, object?>();
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (RmqHeaders.IsRedbHeader(key)) continue;
            if (BasicPropertyNames.Contains(key)) continue;
            if (value is null) continue;

            headers[key] = value switch
            {
                string s => s,
                byte[] b => b,
                int or long or float or double or decimal or bool => value,
                _ => value.ToString()
            };
        }

        if (headers.Count > 0)
            properties.Headers = headers!;

        // Forward AMQP basic properties from headers, mapped by name (the well-known bare names —
        // "ReplyTo", "Priority", "MessageId", "Expiration"/TTL, "Type", "AppId", "UserId", …). Simple
        // string/byte properties go through cached reflection; a `redbRmq.X` prefixed name is also
        // accepted for the consume→produce round-trip. Header wins over any default.
        foreach (var prop in _forwardableProps)
        {
            if (!TryGetHeaderValue(exchange, RmqHeaders.Prefix + prop.Name, prop.Name, out var raw))
                continue;
            try
            {
                prop.SetValue(properties, System.Convert.ChangeType(raw, prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { /* malformed header value — ignore, keep default */ }
        }

        // Explicit cases reflection/Convert.ChangeType can't handle:
        // Timestamp is an AmqpTimestamp (not IConvertible), DeliveryMode/persistence is a flag.
        if (TryGetHeaderValue(exchange, RmqHeaders.Prefix + "Timestamp", "Timestamp", out var tsRaw) && long.TryParse(tsRaw, out var ts))
            properties.Timestamp = new global::RabbitMQ.Client.AmqpTimestamp(ts);

        if (TryGetHeaderValue(exchange, RmqHeaders.Prefix + "Persistent", "Persistent", out var persRaw) && bool.TryParse(persRaw, out var pers))
            properties.Persistent = pers;
        else if (TryGetHeaderValue(exchange, RmqHeaders.Prefix + "DeliveryMode", "DeliveryMode", out var dmRaw) && byte.TryParse(dmRaw, out var dm))
            properties.Persistent = dm == 2; // AMQP: 2 = persistent, 1 = transient

        return (properties, body);
    }

    /// <summary>
    /// Settable string/byte <see cref="BasicProperties"/> mapped from same-named headers via cached
    /// reflection. Excludes <c>Headers</c> (bulk-copied) and things needing special handling
    /// (<c>ContentType</c> — from the message field above; <c>Timestamp</c>/<c>DeliveryMode</c> — explicit).
    /// </summary>
    private static readonly System.Reflection.PropertyInfo[] _forwardableProps = BuildForwardableProps();

    private static System.Reflection.PropertyInfo[] BuildForwardableProps()
    {
        var list = new List<System.Reflection.PropertyInfo>();
        foreach (var p in typeof(BasicProperties).GetProperties())
        {
            if (!p.CanWrite) continue;
            if (p.Name is "Headers" or "ContentType" or "DeliveryMode") continue;
            if (p.PropertyType == typeof(string) || p.PropertyType == typeof(byte))
                list.Add(p);
        }
        return list.ToArray();
    }

    /// <summary>Reads a header by its prefixed (redbRmq.X) name first, then the bare name; true when found non-empty.</summary>
    private static bool TryGetHeaderValue(IExchange exchange, string prefixedKey, string bareKey, out string value)
    {
        if ((exchange.In.Headers.TryGetValue(prefixedKey, out var v) || exchange.In.Headers.TryGetValue(bareKey, out v)) && v is not null)
        {
            value = v.ToString() ?? string.Empty;
            return !string.IsNullOrEmpty(value);
        }
        value = string.Empty;
        return false;
    }

    // ── Helpers ──

    private void EnsureChannel()
    {
        if (_channel is null or { IsOpen: false })
            throw new InvalidOperationException("RabbitMQ channel is not available. The producer might need restart.");
    }

    private static BasicProperties CloneProperties(BasicProperties source)
    {
        var clone = new BasicProperties
        {
            ContentType = source.ContentType,
            ContentEncoding = source.ContentEncoding,
            DeliveryMode = source.DeliveryMode,
            Priority = source.Priority,
            CorrelationId = source.CorrelationId,
            ReplyTo = source.ReplyTo,
            Expiration = source.Expiration,
            MessageId = source.MessageId,
            Timestamp = source.Timestamp,
            Type = source.Type,
            UserId = source.UserId,
            AppId = source.AppId,
            ClusterId = source.ClusterId,
            Persistent = source.Persistent
        };

        if (source.Headers is not null)
            clone.Headers = new Dictionary<string, object?>(source.Headers);

        return clone;
    }

    // ── Publish diagnostics ──

    private void LogPublishDiagnostics(
        string mode, Exception ex, IChannel? channel,
        string? exchangeName, string? routingKey, bool mandatory,
        int bodySize, string? contentType,
        string? correlationId = null, string? replyTo = null, int? timeoutSec = null)
    {
        if (Logger is null) return;

        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════════════");
        sb.AppendLine($"RabbitMQ PUBLISH DIAGNOSTICS ({mode})");
        sb.AppendLine("═══════════════════════════════════════════════════════════");
        sb.AppendLine($"Exception: {ex.GetType().FullName}: {ex.Message}");
        if (ex.InnerException != null)
            sb.AppendLine($"Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");

        sb.AppendLine("───────────────────────────────────────────────────────────");
        sb.AppendLine("CHANNEL STATE:");
        try
        {
            sb.AppendLine($"  IsOpen: {channel?.IsOpen ?? false}");
            sb.AppendLine($"  CloseReason: {channel?.CloseReason?.ReplyText ?? "N/A"}");
        }
        catch (Exception chEx) { sb.AppendLine($"  ERROR reading channel state: {chEx.Message}"); }

        sb.AppendLine("───────────────────────────────────────────────────────────");
        sb.AppendLine("PUBLISH DETAILS:");
        sb.AppendLine($"  Exchange: {exchangeName ?? "(default)"}");
        sb.AppendLine($"  RoutingKey: {routingKey}");
        sb.AppendLine($"  Mandatory: {mandatory}");
        sb.AppendLine($"  BodySize: {bodySize} bytes");
        sb.AppendLine($"  ContentType: {contentType ?? "N/A"}");
        sb.AppendLine($"  Endpoint: {_endpoint.Uri}");

        if (correlationId != null)
        {
            sb.AppendLine("───────────────────────────────────────────────────────────");
            sb.AppendLine("RPC:");
            sb.AppendLine($"  CorrelationId: {correlationId}");
            sb.AppendLine($"  ReplyTo: {replyTo}");
            sb.AppendLine($"  Timeout: {timeoutSec}s");
        }

        sb.AppendLine("───────────────────────────────────────────────────────────");
        sb.AppendLine("POSSIBLE CAUSES:");
        sb.AppendLine("  1. Connection closed (NAT timeout / heartbeat missed)");
        sb.AppendLine("  2. Publisher Confirms timeout");
        sb.AppendLine("  3. Network timeout / connection lost");
        sb.AppendLine("  4. RabbitMQ server overloaded / flow control");
        if (correlationId != null)
            sb.AppendLine("  5. Reply queue deleted (temporary queue autoDelete=true)");
        sb.AppendLine("═══════════════════════════════════════════════════════════");

        Logger.LogError(ex, "{Diagnostics}", sb.ToString());
    }
}

/// <summary>One deferred publish, captured (cloned) when the step ran.</summary>
internal sealed record RabbitMQDeferredPublish(
    string Exchange, string RoutingKey, bool Mandatory, BasicProperties Properties, byte[] Body);

/// <summary>
/// The deferred publishes of one producer in one transacted block. On commit they are published on the producer's
/// transacted channel and committed with one <c>tx.commit</c>, so the block's messages arrive together or not at all.
/// On rollback nothing has been published.
/// </summary>
internal sealed class RabbitMQSendBatch : ITransactedAction
{
    private readonly ConcurrentQueue<RabbitMQDeferredPublish> _publishes = new();
    private readonly Func<CancellationToken, Task<IChannel>> _transactedChannel;
    private readonly SemaphoreSlim _commitLock;
    private readonly ILogger? _logger;

    /// <param name="transactedChannel">The producer's channel in tx mode, opened again when the last one was closed.</param>
    /// <param name="commitLock">
    /// The producer's lock: a channel transaction belongs to the channel, so one batch at a time publishes and commits on
    /// it. Another batch's tx.commit would otherwise commit half of this one.
    /// </param>
    public RabbitMQSendBatch(Func<CancellationToken, Task<IChannel>> transactedChannel, SemaphoreSlim commitLock, ILogger? logger)
    {
        _transactedChannel = transactedChannel;
        _commitLock = commitLock;
        _logger = logger;
    }

    /// <summary>Adds a publish to the batch, after the ones the block deferred before it.</summary>
    public void Add(RabbitMQDeferredPublish publish) => _publishes.Enqueue(publish);

    public async Task Commit(CancellationToken ct = default)
    {
        await _commitLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var channel = await _transactedChannel(ct).ConfigureAwait(false);
            try
            {
                foreach (var p in _publishes)
                {
                    await channel.BasicPublishAsync(
                        exchange: p.Exchange,
                        routingKey: p.RoutingKey,
                        mandatory: p.Mandatory,
                        basicProperties: p.Properties,
                        body: p.Body,
                        cancellationToken: ct).ConfigureAwait(false);
                }

                await channel.TxCommitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Closing the channel discards its open transaction, so none of the batch is delivered, and the next
                // batch starts on a fresh channel instead of committing what this one left behind.
                await CloseAsync(channel).ConfigureAwait(false);
                _logger?.LogError(ex,
                    "RabbitMQ transactional batch failed; none of its {Count} messages was published. First target: " +
                    "exchange={Exchange}, routingKey={RoutingKey}",
                    _publishes.Count, _publishes.FirstOrDefault()?.Exchange, _publishes.FirstOrDefault()?.RoutingKey);
                throw;
            }

            _logger?.LogDebug("RabbitMQ transactional batch committed: messages={Count}", _publishes.Count);
        }
        finally
        {
            _commitLock.Release();
        }
    }

    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("RabbitMQ transactional batch rolled back (messages discarded): messages={Count}", _publishes.Count);
        return Task.CompletedTask;
    }

    private async Task CloseAsync(IChannel channel)
    {
        try
        {
            await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception closeEx)
        {
            _logger?.LogWarning(closeEx,
                "RabbitMQ: closing the transacted channel after a failed batch failed; it is dropped and replaced.");
        }
        finally
        {
            channel.Dispose();
        }
    }
}
