using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.RabbitMQ;

/// <summary>
/// RabbitMQ endpoint. The queue name comes from <see cref="EndpointUri.Path"/>.
/// Manages connection and channel lifecycle for a single URI.
/// Supports publisher confirms, automatic recovery, and transacted channels.
/// </summary>
public sealed class RabbitMQEndpoint : EndpointBase<RabbitMQEndpointOptions>
{
    private readonly RabbitMQComponent _component;
    private readonly List<IChannel> _channels = new();

    /// <summary>Queue name from the URI path (may be empty for auto-generated queues).</summary>
    public string QueueName { get; }

    /// <summary>Logger from the parent component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Typed options (exposed for consumer/producer).</summary>
    internal RabbitMQEndpointOptions EndpointOptions => Options;

    /// <summary>Creates a RabbitMQ endpoint.</summary>
    public RabbitMQEndpoint(EndpointUri uri, RabbitMQComponent component, RabbitMQEndpointOptions options)
        : base(uri, component, options)
    {
        _component = component ?? throw new ArgumentNullException(nameof(component));
        QueueName = uri.Path;
        Logger = component.Logger;

        // Default exchange routes by queue name — if RoutingKey is not explicitly set,
        // use QueueName so publish is routed correctly.
        if (string.IsNullOrEmpty(options.Exchange) && string.IsNullOrEmpty(options.RoutingKey)
            && !string.IsNullOrEmpty(QueueName))
        {
            options.RoutingKey = QueueName;
        }
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new RabbitMQProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor) => new RabbitMQConsumer(this, processor, Options);

    /// <summary>
    /// Creates a new channel on the shared connection. Thread-safe.
    /// Each producer/consumer MUST get its own channel to avoid deadlocks
    /// (e.g. consumer processing a message that publishes on the same channel blocks confirms).
    /// Publisher confirms are enabled by default but must be disabled for transacted channels
    /// (RabbitMQ does not allow confirm + tx mode on the same channel).
    /// </summary>
    /// <param name="publisherConfirms">Enable publisher confirms (default true). Set to false for transacted channels.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<IChannel> CreateChannelAsync(bool publisherConfirms = true, CancellationToken ct = default)
    {
        var connection = await _component.GetOrCreateConnectionAsync(this, ct).ConfigureAwait(false);

        // For confirm-tracking channels we MUST supply an explicit ThrottlingRateLimiter sized
        // to MaxOutstandingConfirms. RabbitMQ.Client 7.x defaults to a tiny built-in limiter
        // that surfaces as "Could not acquire a lease from the rate limiter" under publish bursts.
        var channelOpts = publisherConfirms
            ? new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true,
                outstandingPublisherConfirmationsRateLimiter: new ThrottlingRateLimiter(
                    Options.MaxOutstandingConfirms > 0 ? Options.MaxOutstandingConfirms : 2048))
            : new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                outstandingPublisherConfirmationsRateLimiter: null);

        var channel = await connection.CreateChannelAsync(channelOpts, ct).ConfigureAwait(false);

        AttachChannelLifecycleHandlers(channel);

        lock (_channels) { _channels.Add(channel); }

        Logger?.LogDebug("RabbitMQ channel created (confirms={Confirms})", publisherConfirms);
        return channel;
    }

    /// <summary>
    /// Subscribes diagnostic logging to channel-level events. Without these handlers,
    /// failures like "message not routed" (BasicReturn), broker-side channel shutdown,
    /// and callback exceptions are silently swallowed by the .NET client.
    /// </summary>
    private void AttachChannelLifecycleHandlers(IChannel channel)
    {
        channel.ChannelShutdownAsync += (sender, args) =>
        {
            if (args.Initiator == ShutdownInitiator.Application)
            {
                Logger?.LogDebug("RabbitMQ channel shutdown by application: {Reason}", args.ReplyText);
            }
            else
            {
                Logger?.LogWarning(
                    "RabbitMQ channel shutdown unexpectedly: initiator={Initiator}, code={Code}, reason={Reason}",
                    args.Initiator, args.ReplyCode, args.ReplyText);
            }
            return Task.CompletedTask;
        };

        channel.CallbackExceptionAsync += (sender, args) =>
        {
            Logger?.LogError(args.Exception, "RabbitMQ channel callback exception in {Detail}", args.Detail);
            return Task.CompletedTask;
        };

        channel.BasicReturnAsync += (sender, args) =>
        {
            // amq.gen-* are auto-generated, exclusive RPC reply queues. After a client
            // times out, its reply queue vanishes and any in-flight reply targeting it
            // gets returned by the broker — this is normal, not a problem worth a Warning.
            var isTransientReplyTo = !string.IsNullOrEmpty(args.RoutingKey)
                && args.RoutingKey.StartsWith("amq.gen-", StringComparison.Ordinal);

            if (isTransientReplyTo)
            {
                Logger?.LogDebug(
                    "RabbitMQ BasicReturn (transient reply-to): routingKey={RoutingKey}, code={Code}, reason={Reason}",
                    args.RoutingKey, args.ReplyCode, args.ReplyText);
            }
            else
            {
                Logger?.LogWarning(
                    "RabbitMQ BasicReturn: exchange={Exchange}, routingKey={RoutingKey}, code={Code}, reason={Reason}, bodySize={Size} — message NOT routed",
                    args.Exchange, args.RoutingKey, args.ReplyCode, args.ReplyText, args.Body.Length);
            }
            return Task.CompletedTask;
        };

        channel.BasicNacksAsync += (sender, args) =>
        {
            Logger?.LogWarning(
                "RabbitMQ BasicNack from broker: deliveryTag={Tag}, multiple={Multiple} — publish was rejected",
                args.DeliveryTag, args.Multiple);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Subscribes diagnostic logging to connection-level events. Invoked by
    /// <see cref="RabbitMQComponent"/> exactly once per pooled connection.
    /// </summary>
    internal void AttachConnectionLifecycleHandlers(IConnection connection)
    {
        connection.ConnectionShutdownAsync += (sender, args) =>
        {
            if (args.Initiator == ShutdownInitiator.Application)
            {
                Logger?.LogInformation("RabbitMQ connection shutdown by application: {Reason}", args.ReplyText);
            }
            else
            {
                Logger?.LogWarning(
                    "RabbitMQ connection shutdown unexpectedly: initiator={Initiator}, code={Code}, reason={Reason}",
                    args.Initiator, args.ReplyCode, args.ReplyText);
            }
            return Task.CompletedTask;
        };

        connection.RecoverySucceededAsync += (sender, args) =>
        {
            Logger?.LogInformation("RabbitMQ connection recovery SUCCEEDED: host={Host}", Options.Host);
            return Task.CompletedTask;
        };

        connection.ConnectionBlockedAsync += (sender, args) =>
        {
            Logger?.LogWarning("RabbitMQ connection BLOCKED by broker: reason={Reason} — publish paused", args.Reason);
            return Task.CompletedTask;
        };

        connection.ConnectionUnblockedAsync += (sender, args) =>
        {
            Logger?.LogInformation("RabbitMQ connection UNBLOCKED — publish resumed");
            return Task.CompletedTask;
        };

        connection.CallbackExceptionAsync += (sender, args) =>
        {
            Logger?.LogError(args.Exception, "RabbitMQ connection callback exception in {Detail}", args.Detail);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Declares exchange, queue, and bindings according to endpoint options.
    /// </summary>
    internal async Task DeclareTopologyAsync(IChannel channel, CancellationToken ct = default)
    {
        if (!Options.Declare) return;

        var exchangeName = Options.Exchange;
        var queueName = QueueName;
        var routingKey = Options.RoutingKey;

        // 1. Declare exchange if named
        if (!string.IsNullOrEmpty(exchangeName))
        {
            await channel.ExchangeDeclareAsync(
                exchange: exchangeName,
                type: Options.ExchangeType,
                durable: Options.ExchangeDurable,
                autoDelete: Options.ExchangeAutoDelete,
                arguments: null,
                cancellationToken: ct).ConfigureAwait(false);

            Logger?.LogDebug("RabbitMQ exchange declared: {Exchange} (type={Type})", exchangeName, Options.ExchangeType);
        }

        // 2. Declare queue
        var isTemporary = string.IsNullOrEmpty(queueName);
        var queueArgs = Options.BuildQueueArguments();

        var result = await channel.QueueDeclareAsync(
            queue: queueName,
            durable: !isTemporary && Options.Durable,
            exclusive: isTemporary || Options.Exclusive,
            autoDelete: isTemporary || Options.AutoDelete,
            arguments: queueArgs.Count > 0 ? queueArgs! : null,
            cancellationToken: ct).ConfigureAwait(false);

        Logger?.LogDebug("RabbitMQ queue declared: {Queue} (durable={Durable})", result.QueueName, Options.Durable);

        // 3. Bind queue to exchange
        if (!string.IsNullOrEmpty(exchangeName))
        {
            await channel.QueueBindAsync(
                queue: result.QueueName,
                exchange: exchangeName,
                routingKey: routingKey,
                arguments: null,
                cancellationToken: ct).ConfigureAwait(false);

            Logger?.LogDebug("RabbitMQ queue bound: {Queue} → {Exchange} (routingKey={RoutingKey})",
                result.QueueName, exchangeName, routingKey);
        }
    }

    /// <inheritdoc />
    public override async Task Stop(CancellationToken ct = default)
    {
        // Close channels owned by this endpoint. The pooled IConnection is owned by
        // RabbitMQComponent and is NOT closed here — it survives Stop/Start cycles and is
        // released only on RouteContext.DisposeAsync (which calls Component.DisposeAsync).
        List<IChannel> channelsCopy;
        lock (_channels) { channelsCopy = new List<IChannel>(_channels); _channels.Clear(); }

        foreach (var ch in channelsCopy)
        {
            if (ch is { IsOpen: true })
            {
                try { await ch.CloseAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { Logger?.LogWarning(ex, "Error closing RabbitMQ channel"); }
            }
            try { ch.Dispose(); }
            catch (Exception ex) { Logger?.LogDebug(ex, "Error disposing RabbitMQ channel"); }
        }

        Logger?.LogInformation("RabbitMQ endpoint stopped: {Uri}", Uri);
    }
}
