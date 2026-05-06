using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Amqp;
using Amqp.Framing;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using AmqpMessage = global::Amqp.Message;
using RouteMessage = redb.Route.Core.Message;

namespace redb.Route.Amqp;

/// <summary>
/// AMQP 1.0 consumer. Receives messages via <see cref="ReceiverLink"/> with support for:
/// concurrent processing, automatic/manual accept, and RPC reply.
/// </summary>
public sealed class AmqpConsumer : IConsumer
{
    private readonly AmqpEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private readonly AmqpEndpointOptions _options;
    private ILogger? _logger;
    private readonly SemaphoreSlim _semaphore;

    private ReceiverLink? _receiver;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly InflightDrainGuard _drain = new();

    /// <summary>Number of messages successfully processed.</summary>
    public long ProcessedCount { get; private set; }

    /// <summary>Creates an AMQP consumer.</summary>
    public AmqpConsumer(AmqpEndpoint endpoint, IProcessor processor, AmqpEndpointOptions options)
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
        _receiver = await _endpoint.CreateReceiverLinkAsync(ct: ct).ConfigureAwait(false);

        _cts = new CancellationTokenSource();
        _drain.Start(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation(
            "AMQP consumer started: address={Address}, credit={Credit}, concurrent={Concurrent}",
            _endpoint.Address, _options.Credit, _options.ConcurrentConsumers);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // Step 1: stop accepting new messages
        _cts?.Cancel();

        // Close receiver BEFORE awaiting the loop — this unblocks
        // ReceiveAsync (which doesn't accept CancellationToken).
        if (_receiver is { IsClosed: false })
        {
            try { await _receiver.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Error closing AMQP receiver link"); }
        }
        _receiver = null;

        // Step 2: drain in-flight processing
        await _drain.DrainAsync(ct, _logger, $"amqp:{_endpoint.Address}").ConfigureAwait(false);

        if (_receiveTask != null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _drain.Dispose();

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("AMQP consumer stopped: address={Address}", _endpoint.Address);
    }

    // ── Receive loop ──

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var receiveTimeout = _options.ReceiveTimeout > 0
            ? TimeSpan.FromSeconds(_options.ReceiveTimeout)
            : TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested)
        {
            var receiver = _receiver;
            if (receiver is null or { IsClosed: true }) break;

            AmqpMessage? msg = null;
            try
            {
                msg = await receiver.ReceiveAsync(receiveTimeout).ConfigureAwait(false);
                if (msg == null) continue;

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
            catch (AmqpException ex) when (ct.IsCancellationRequested)
            {
                _logger?.LogDebug(ex, "AMQP receive cancelled during shutdown");
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in AMQP receive loop: address={Address}",
                    _endpoint.Address);

                if (msg != null && receiver is { IsClosed: false })
                {
                    try { receiver.Release(msg); }
                    catch (Exception relEx) { _logger?.LogWarning(relEx, "AMQP: failed to release message after error"); }
                }
            }
        }
    }

    private async Task ProcessMessageAsync(AmqpMessage msg, CancellationToken ct)
    {
        using var activity = StartConsumerActivity(msg);

        var exchange = CreateExchange(msg);

        var receiver = _receiver;
        if (receiver is null or { IsClosed: true })
        {
            _logger?.LogWarning("AMQP: receiver closed before message could be processed");
            return;
        }

        // Register deferred accept action for transacted mode
        var ackAction = new AmqpAckAction(receiver, msg, _logger);
        RegisterTransactedAction(exchange, $"amqp-ack-{Guid.NewGuid():N}", ackAction);

        try
        {
            await _processor.Process(exchange, ct).ConfigureAwait(false);

            // RPC reply
            if (!string.IsNullOrEmpty(msg.Properties?.ReplyTo))
            {
                await SendReplyAsync(exchange, msg.Properties.ReplyTo, msg.Properties.CorrelationId?.ToString())
                    .ConfigureAwait(false);
            }

            // Accept (settle) the message
            if (_options.AutoAccept && receiver is { IsClosed: false })
            {
                receiver.Accept(msg);
            }

            ProcessedCount++;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AMQP message processing error: address={Address}, messageId={MessageId}",
                _endpoint.Address, msg.Properties?.MessageId);

            if (receiver is { IsClosed: false })
            {
                try { receiver.Release(msg); }
                catch (Exception releaseEx) { _logger?.LogWarning(releaseEx, "Error releasing AMQP message"); }
            }
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Trace context propagation ──

    private Activity? StartConsumerActivity(AmqpMessage msg)
    {
        var propagator = DistributedContextPropagator.Current;
        var appProps = msg.ApplicationProperties?.Map;

        propagator.ExtractTraceIdAndState(appProps,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not global::Amqp.Types.Map map) return;
                if (!map.TryGetValue(key, out var raw) || raw is null) return;
                value = raw.ToString();
            },
            out var traceParent,
            out var traceState);

        ActivityContext parentContext = default;
        if (!string.IsNullOrEmpty(traceParent))
            ActivityContext.TryParse(traceParent, traceState, out parentContext);

        var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.Address} receive",
            ActivityKind.Consumer,
            parentContext);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "amqp");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", _endpoint.Address);
            if (msg.Properties?.MessageId is { } msgId)
                activity.SetTag("messaging.message.id", msgId);
            if (msg.Properties?.Subject is { } subject)
                activity.SetTag("messaging.amqp.subject", subject);
        }

        return activity;
    }

    // ── Exchange creation ──

    private Exchange CreateExchange(AmqpMessage msg)
    {
        var body = AmqpMessageHelper.ExtractBody(msg);
        var routeMsg = new RouteMessage(body);

        AmqpMessageHelper.CopyApplicationProperties(msg, routeMsg);

        // AMQP metadata as redbAmqp.* headers
        routeMsg.Headers[AmqpHeaders.Address] = _endpoint.Address;

        if (msg.Properties != null)
        {
            if (msg.Properties.MessageId != null)
                routeMsg.Headers[AmqpHeaders.MessageId] = msg.Properties.MessageId.ToString()!;
            if (msg.Properties.CorrelationId != null)
                routeMsg.Headers[AmqpHeaders.CorrelationId] = msg.Properties.CorrelationId.ToString()!;
            if (!string.IsNullOrEmpty(msg.Properties.ReplyTo))
                routeMsg.Headers[AmqpHeaders.ReplyTo] = msg.Properties.ReplyTo;
            if (!string.IsNullOrEmpty(msg.Properties.ContentType))
            {
                routeMsg.Headers[AmqpHeaders.ContentType] = msg.Properties.ContentType;
                routeMsg.ContentType = msg.Properties.ContentType;
            }
            if (!string.IsNullOrEmpty(msg.Properties.Subject))
                routeMsg.Headers[AmqpHeaders.Subject] = msg.Properties.Subject;
            if (!string.IsNullOrEmpty(msg.Properties.GroupId))
                routeMsg.Headers[AmqpHeaders.GroupId] = msg.Properties.GroupId;
            if (msg.Properties.GroupSequence > 0)
                routeMsg.Headers[AmqpHeaders.GroupSequence] = msg.Properties.GroupSequence;
            if (msg.Properties.CreationTime != default)
                routeMsg.Headers[AmqpHeaders.CreationTime] = msg.Properties.CreationTime;
            if (msg.Properties.AbsoluteExpiryTime != default)
                routeMsg.Headers[AmqpHeaders.AbsoluteExpiryTime] = msg.Properties.AbsoluteExpiryTime;
        }

        if (msg.Header != null)
        {
            routeMsg.Headers[AmqpHeaders.Durable] = msg.Header.Durable;
            routeMsg.Headers[AmqpHeaders.Priority] = msg.Header.Priority;
            if (msg.Header.Ttl > 0)
                routeMsg.Headers[AmqpHeaders.Ttl] = msg.Header.Ttl;
            routeMsg.Headers[AmqpHeaders.DeliveryCount] = msg.Header.DeliveryCount;
            routeMsg.Headers[AmqpHeaders.FirstAcquirer] = msg.Header.FirstAcquirer;
        }

        var pattern = string.IsNullOrEmpty(msg.Properties?.ReplyTo)
            ? ExchangePattern.InOnly
            : ExchangePattern.InOut;

        var exchange = Exchange.Create(routeMsg, _endpoint.ScopeFactory);
        exchange.Pattern = pattern;
        return exchange;
    }

    // ── RPC reply ──

    private async Task SendReplyAsync(IExchange exchange, string replyTo, string? correlationId)
    {
        try
        {
            var responseBody = exchange.HasOut
                ? exchange.Out!.Body
                : exchange.In.Body;

            object wireBody = responseBody switch
            {
                byte[] bytes => (object)bytes,
                string str   => str,
                null         => Array.Empty<byte>(),
                var other    => other.ToString() ?? string.Empty
            };

            var reply = new AmqpMessage(wireBody)
            {
                Properties = new Properties { ContentType = exchange.In.ContentType ?? _options.ContentType }
            };

            if (!string.IsNullOrEmpty(correlationId))
                reply.Properties.CorrelationId = correlationId;

            // Copy response headers
            var responseHeaders = exchange.HasOut ? exchange.Out!.Headers : exchange.In.Headers;
            var appProps = new ApplicationProperties();
            foreach (var (key, value) in responseHeaders)
            {
                if (AmqpHeaders.IsRedbHeader(key)) continue;
                if (value is null) continue;
                appProps[key] = value switch
                {
                    string s => s,
                    int or long or float or double or bool => value,
                    _ => value.ToString()!
                };
            }
            if (appProps.Map.Count > 0)
                reply.ApplicationProperties = appProps;

            var replySender = new SenderLink(
                _endpoint.CurrentSession!,
                $"reply-sender-{Guid.NewGuid():N}",
                replyTo);

            // Bounded send timeout: a dead reply address (client gone, queue removed)
            // must not block the consumer slot indefinitely. The original message will
            // still be accepted by the caller — the client will see its own RPC timeout.
            var sendTimeout = TimeSpan.FromSeconds(_options.ReplyTimeout > 0 ? _options.ReplyTimeout : 2);
            await replySender.SendAsync(reply, sendTimeout).ConfigureAwait(false);
            try { await replySender.CloseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception closeEx)
            {
                _logger?.LogWarning(closeEx, "AMQP: error closing reply sender, forcing close");
                replySender.Close(TimeSpan.Zero);
            }

            _logger?.LogDebug("AMQP RPC reply sent: replyTo={ReplyTo}, correlationId={CorrelationId}",
                replyTo, correlationId);
        }
        catch (Exception ex)
        {
            // Swallow: failed reply must not prevent accepting the original message.
            // Client will detect failure via its own RPC timeout.
            _logger?.LogWarning(ex, "AMQP: failed to send RPC reply to {ReplyTo} (correlationId={CorrelationId}) — original message will still be accepted",
                replyTo, correlationId);
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

/// <summary>Deferred AMQP acknowledgement action.</summary>
internal sealed class AmqpAckAction : ITransactedAction
{
    private readonly ReceiverLink _receiver;
    private readonly AmqpMessage _msg;
    private readonly ILogger? _logger;

    public AmqpAckAction(ReceiverLink receiver, AmqpMessage msg, ILogger? logger)
    {
        _receiver = receiver;
        _msg = msg;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Commit(CancellationToken ct = default)
    {
        _receiver.Accept(_msg);
        _logger?.LogDebug("AMQP ack committed");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Rollback(CancellationToken ct = default)
    {
        _receiver.Release(_msg);
        _logger?.LogDebug("AMQP nack (release/rollback)");
        return Task.CompletedTask;
    }
}
