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

    // The node the consumer reads: the URI address, or the name the broker gave a dynamic node.
    private string _address;

    /// <summary>
    /// The node this consumer reads: the address of the URI, or, with <c>dynamic=true</c>, the name the broker gave
    /// the node it created (known once <see cref="Start"/> has returned). Senders reach the consumer at this address.
    /// </summary>
    public string Address => _address;
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
        _address = endpoint.Address;
        _headerFilter = endpoint.HeaderFilterStrategy;
    }

    private readonly IHeaderFilterStrategy _headerFilter;

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        var workerCount = Math.Max(1, _options.ResolvedConcurrentConsumers);
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;

        // Each worker is a receiver link of its own. On a MULTICAST (topic) address every link is a subscription
        // that gets a COPY of each message, and every dynamic link gets a node of its own: N workers would not
        // share the work, they would process each message N times or read N different nodes. One link, as IBM MQ
        // does for a topic.
        var multicast = _options.ResolveCapabilities().Contains("topic", StringComparer.OrdinalIgnoreCase);
        if (workerCount > 1 && (multicast || _options.Dynamic))
        {
            _logger?.LogWarning(
                "AMQP: ConcurrentConsumers({N}) ignored for {Kind} {Address}: each receiver link would get a copy of "
                + "every message instead of a share of them, so the consumer runs one link",
                workerCount, multicast ? "MULTICAST address" : "a dynamic node", _address);
            workerCount = 1;
        }

        _cts = new CancellationTokenSource();
        _drain.Start(ct);

        try
        {
            for (var i = 0; i < workerCount; i++)
            {
                // Every link waits for the broker's attach answer before Start returns. Without the wait a refused
                // link (an unknown address, no permission) was only logged by the receive loop and the consumer stayed
                // "started" without receiving, and on a topic the subscription did not exist yet when Start returned.
                var attached = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var (session, receiver) = await _endpoint
                    .CreateDedicatedReceiverAsync($"receiver-{_endpoint.Address}-{i}-{Guid.NewGuid():N}",
                        (link, remote) => OnLinkAttached(link, remote, attached), ct)
                    .ConfigureAwait(false);
                var worker = new Worker(session, receiver);
                _workers.Add(worker);

                string? address;
                try
                {
                    address = await attached.Task
                        .WaitAsync(TimeSpan.FromSeconds(_options.Timeout), ct).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new InvalidOperationException(
                        $"{ConsumerName} cannot start: the broker did not answer the receiver link within "
                        + $"{_options.Timeout} s.", ex);
                }

                if (_options.Dynamic)
                {
                    // The broker names the dynamic node in its attach answer; the route reads that node.
                    if (string.IsNullOrEmpty(address))
                        throw new InvalidOperationException(
                            $"{ConsumerName} cannot start: the broker created no dynamic node for the receiver link.");
                    _address = address;
                }

                worker.Loop = Task.Run(() => ReceiveLoopAsync(worker, _cts.Token), _cts.Token);
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

        _logger?.LogInformation(
            "AMQP consumer started: address={Address}, credit={Credit}, concurrent={Concurrent}",
            _address, _options.Credit, workerCount);
    }

    /// <summary>The consumer as errors name it: its scheme and the address of its URI.</summary>
    private string ConsumerName => $"AMQP consumer amqp:{_endpoint.Address}";

    /// <summary>
    /// The broker's attach answer. An accepted receiver link comes back with a source (for a dynamic node, the address
    /// the broker gave it); a refused one comes back without a source and is detached right after with the reason.
    /// Frames of a connection are handled one after another, so the detach cannot pass the handler subscribed here.
    /// </summary>
    private void OnLinkAttached(ILink link, Attach remote, TaskCompletionSource<string?> attached)
    {
        if (remote.Source is Source source)
        {
            attached.TrySetResult(source.Address);
            return;
        }

        link.Closed += (_, error) => attached.TrySetException(Refused(error));
        if (link.IsClosed)
            attached.TrySetException(Refused(link.Error));
    }

    private InvalidOperationException Refused(Error? error) => new(
        $"{ConsumerName} cannot start: the broker refused the receiver link"
        + (error is null ? "." : $" ({error.Condition}: {error.Description})."));

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
        await _drain.DrainAsync(ct, _logger, $"amqp:{_address}").ConfigureAwait(false);

        await StopWorkersAsync(ct).ConfigureAwait(false);

        _cts?.Dispose();
        _cts = null;
        _drain.Dispose();

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("AMQP consumer stopped: address={Address}", _address);
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
                // Transport-level failure - invisible to the core's statistics wrappers, so the
                // endpoint records it itself. Pipeline errors stay with StatisticsProcessor.
                _endpoint.RecordError(ex);
                _logger?.LogError(ex, "Error in AMQP receive loop: address={Address}",
                    _address);

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
        using var span = StartConsumerSpan(msg);

        var exchange = CreateExchange(msg);

        var receiver = worker.Receiver;
        if (receiver is null or { IsClosed: true })
        {
            _logger?.LogWarning("AMQP: receiver closed before message could be processed");
            return;
        }

        // The delivery is settled by this consumer once the whole unit of work ended well, not by the route
        // transaction: the transaction owns the database and the outgoing sends.
        var ackAction = new AmqpAckAction(receiver, msg, _logger);

        // ackMode=auto: settled on receipt, before the route runs; a failure below does not bring it back.
        if (_options.AckMode == AckMode.Auto)
            await ackAction.Commit(ct).ConfigureAwait(false);

        var pipelineFailed = false;
        try
        {
            try
            {
                await _processor.Process(exchange, ct).ConfigureAwait(false);
                exchange.ThrowIfUnhandledFailure();
            }
            catch
            {
                pipelineFailed = true;
                throw;
            }

            // RPC reply
            if (!string.IsNullOrEmpty(msg.Properties?.ReplyTo))
            {
                await SendReplyAsync(worker.Session, exchange, msg.Properties.ReplyTo, msg.Properties.CorrelationId?.ToString())
                    .ConfigureAwait(false);
            }

            // Accept (settle) the message
            // Settle through the ack action: a route-level .Transacted() may already have accepted it (Settled).
            if (_options.AckMode == AckMode.Manual && !ackAction.Settled && receiver is { IsClosed: false })
            {
                await ackAction.Commit(ct).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _processedCount);
        }
        catch (Exception ex)
        {
            // Our own stop (the processing token) is not a failure; any other cancellation is.
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                span.Activity.RecordFailure(ex);
            // A pipeline failure is already counted by the core's StatisticsProcessor; a settle
            // failure AFTER a successful pipeline is invisible to the core, so the transport
            // records it (ревью дуги, M13).
            if (!pipelineFailed)
                _endpoint.RecordError(ex);
            _logger?.LogError(ex, "AMQP message processing error: address={Address}, messageId={MessageId}",
                _address, msg.Properties?.MessageId);

            if (receiver is { IsClosed: false })
            {
                try { await ackAction.Rollback(ct).ConfigureAwait(false); }
                catch (Exception releaseEx) { _logger?.LogWarning(releaseEx, "Error releasing AMQP message"); }
            }
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Trace context propagation ──

    private TransportSpan StartConsumerSpan(AmqpMessage msg)
    {
        var span = RouteTelemetryExtensions.StartConsumerSpan(
            (_endpoint.Component as ComponentBase)?.Context,
            $"{_address} receive", ActivityKind.Consumer,
            "messaging.system", "amqp", _endpoint.Uri.NormalizedKey,
            msg.ApplicationProperties?.Map,
            static (map, name) => map is not null && map.TryGetValue(name, out var raw) ? raw?.ToString() : null,
            destination: _address, operation: "receive");

        if (span.Activity is { IsAllDataRequested: true } activity)
        {
            if (msg.Properties?.MessageId is { } msgId)
                activity.SetTag("messaging.message.id", msgId);
            if (msg.Properties?.Subject is { } subject)
                activity.SetTag("messaging.amqp.subject", subject);
        }

        return span;
    }

    // ── Exchange creation ──

    private Exchange CreateExchange(AmqpMessage msg)
    {
        var body = AmqpMessageHelper.ExtractBody(msg);
        var routeMsg = new RouteMessage(body);
        var exchange = Exchange.Create(routeMsg, _endpoint.ScopeFactory);

        AmqpMessageHelper.CopyApplicationProperties(msg, routeMsg, _headerFilter, exchange);

        // Standard AMQP properties → bare header names (self-documenting, round-trip via the producer);
        // transport metadata (Address/DeliveryCount/FirstAcquirer) → redbAmqp.* (no standard name).
        routeMsg.Headers[AmqpHeaders.Address] = _address;

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
            if (!string.IsNullOrEmpty(msg.Properties.ContentEncoding))
                routeMsg.Headers[AmqpHeaders.ContentEncoding] = msg.Properties.ContentEncoding;
            if (!string.IsNullOrEmpty(msg.Properties.To))
                routeMsg.Headers[AmqpHeaders.To] = msg.Properties.To;
            if (!string.IsNullOrEmpty(msg.Properties.ReplyToGroupId))
                routeMsg.Headers[AmqpHeaders.ReplyToGroupId] = msg.Properties.ReplyToGroupId;
            if (msg.Properties.UserId is { Length: > 0 } userId)
                routeMsg.Headers[AmqpHeaders.UserId] = System.Text.Encoding.UTF8.GetString(userId);
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

            // Response headers → application-properties, as the endpoint's strategy lets them out
            var responseHeaders = exchange.HasOut ? exchange.Out!.Headers : exchange.In.Headers;
            var appProps = new ApplicationProperties();
            foreach (var (key, value) in responseHeaders)
            {
                if (value is null) continue;
                if (_headerFilter.FilterOutgoing(key, value, exchange)) continue;
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

}

/// <summary>Deferred AMQP acknowledgement action.</summary>
internal sealed class AmqpAckAction : ITransactedAction
{
    private int _settled;

    /// <summary>True once the delivery was accepted or released, by either the deferred (transaction) or the inline path.</summary>
    public bool Settled => Volatile.Read(ref _settled) == 1;

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
        if (Interlocked.Exchange(ref _settled, 1) != 0) return Task.CompletedTask;
        _receiver.Accept(_msg);
        _logger?.LogDebug("AMQP ack committed");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Rollback(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return Task.CompletedTask;
        _receiver.Release(_msg);
        _logger?.LogDebug("AMQP nack (release/rollback)");
        return Task.CompletedTask;
    }
}
