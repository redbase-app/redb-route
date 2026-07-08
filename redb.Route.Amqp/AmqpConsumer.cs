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

    // ConcurrentConsumers is honoured as N REAL competing consumers: each Worker owns its own AMQP
    // session + receiver link and drives a single serial receive loop. AMQPNetLite sessions/links are
    // not thread-safe, so concurrency comes from N independent workers, never from sharing one link.
    private readonly List<Worker> _workers = new();
    private CancellationTokenSource? _cts;
    private readonly InflightDrainGuard _drain = new();

    private long _processedCount;
    /// <summary>Number of messages successfully processed (summed across all worker loops).</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>One competing consumer: its own AMQP session, receiver link and receive-loop task.</summary>
    private sealed class Worker
    {
        public Worker(Session session, ReceiverLink receiver) { Session = session; Receiver = receiver; }
        public Session Session { get; }
        public ReceiverLink Receiver { get; }
        public Task? Loop { get; set; }
    }

    /// <summary>Creates an AMQP consumer.</summary>
    public AmqpConsumer(AmqpEndpoint endpoint, IProcessor processor, AmqpEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = endpoint.Logger;
    }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        var workerCount = Math.Max(1, _options.ConcurrentConsumers);

        _cts = new CancellationTokenSource();
        _drain.Start(ct);

        try
        {
            for (var i = 0; i < workerCount; i++)
            {
                var (session, receiver) = await _endpoint
                    .CreateDedicatedReceiverAsync($"receiver-{_endpoint.Address}-{i}-{Guid.NewGuid():N}", ct)
                    .ConfigureAwait(false);
                var worker = new Worker(session, receiver);
                worker.Loop = Task.Run(() => ReceiveLoopAsync(worker, _cts.Token), _cts.Token);
                _workers.Add(worker);
            }
        }
        catch
        {
            // Partial start — tear down whatever came up so we don't leak sessions/links.
            await StopWorkersAsync(ct).ConfigureAwait(false);
            _drain.Dispose();
            _cts?.Dispose();
            _cts = null;
            throw;
        }

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation(
            "AMQP consumer started: address={Address}, credit={Credit}, concurrent={Concurrent}",
            _endpoint.Address, _options.Credit, workerCount);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // Step 1: stop accepting new messages
        _cts?.Cancel();

        // Close receivers BEFORE awaiting the loops — this unblocks ReceiveAsync (which doesn't
        // accept a CancellationToken). Then drain in-flight processing, then await the loops.
        foreach (var w in _workers)
        {
            if (w.Receiver is { IsClosed: false })
            {
                try { await w.Receiver.CloseAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Error closing AMQP receiver link"); }
            }
        }

        // Step 2: drain in-flight processing
        await _drain.DrainAsync(ct, _logger, $"amqp:{_endpoint.Address}").ConfigureAwait(false);

        await StopWorkersAsync(ct).ConfigureAwait(false);

        _cts?.Dispose();
        _cts = null;
        _drain.Dispose();

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("AMQP consumer stopped: address={Address}", _endpoint.Address);
    }

    /// <summary>Awaits every worker loop and closes its receiver + dedicated session.</summary>
    private async Task StopWorkersAsync(CancellationToken ct)
    {
        foreach (var w in _workers)
        {
            if (w.Loop != null)
            {
                try { await w.Loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex) { _logger?.LogDebug(ex, "AMQP worker loop faulted during stop"); }
            }

            if (w.Receiver is { IsClosed: false })
            {
                try { await w.Receiver.CloseAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogDebug(ex, "Error closing AMQP receiver link during stop"); }
            }

            if (w.Session is { IsClosed: false })
            {
                try { await w.Session.CloseAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogDebug(ex, "Error closing AMQP worker session during stop"); }
            }
        }
        _workers.Clear();
    }

    // ── Receive loop ──

    private async Task ReceiveLoopAsync(Worker worker, CancellationToken ct)
    {
        var receiveTimeout = _options.ReceiveTimeout > 0
            ? TimeSpan.FromSeconds(_options.ReceiveTimeout)
            : TimeSpan.FromSeconds(60);

        var receiver = worker.Receiver;

        while (!ct.IsCancellationRequested)
        {
            if (receiver is null or { IsClosed: true }) break;

            AmqpMessage? msg = null;
            try
            {
                msg = await receiver.ReceiveAsync(receiveTimeout).ConfigureAwait(false);
                if (msg == null) continue;

                _drain.Increment();
                try
                {
                    await ProcessMessageAsync(worker, msg, _drain.ProcessingToken).ConfigureAwait(false);
                }
                finally
                {
                    _drain.Decrement();
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

    private async Task ProcessMessageAsync(Worker worker, AmqpMessage msg, CancellationToken ct)
    {
        using var activity = StartConsumerActivity(msg);

        var exchange = CreateExchange(msg);

        var receiver = worker.Receiver;
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
                await SendReplyAsync(worker.Session, exchange, msg.Properties.ReplyTo, msg.Properties.CorrelationId?.ToString())
                    .ConfigureAwait(false);
            }

            // Accept (settle) the message
            if (_options.AutoAccept && receiver is { IsClosed: false })
            {
                receiver.Accept(msg);
            }

            Interlocked.Increment(ref _processedCount);
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

    private async Task SendReplyAsync(Session session, IExchange exchange, string replyTo, string? correlationId)
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

            // Reply on the WORKER's own session (never the shared endpoint session) — each worker
            // runs on its own AMQP session, so concurrent replies never race on a shared session.
            var replySender = new SenderLink(
                session,
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
