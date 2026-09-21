using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Transactions;
using Amqp;
using Amqp.Framing;
using Amqp.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Transactions;
using redb.Route.Core;
using redb.Route.Telemetry;
using AmqpMessage = global::Amqp.Message;
using RouteMessage = redb.Route.Core.Message;

namespace redb.Route.Amqp;

/// <summary>
/// AMQP 1.0 producer. Sends messages via a <see cref="SenderLink"/> with support for:
/// <list type="bullet">
///   <item><b>Immediate</b> — direct send via SenderLink.SendAsync.</item>
///   <item><b>Transactional</b> — deferred send via <see cref="ITransactedAction"/>.</item>
///   <item><b>RPC</b> — request/reply with a temporary dynamic receiver link.</item>
/// </list>
/// </summary>
public sealed class AmqpProducer : ConnectableProducer
{
    private readonly AmqpEndpoint _endpoint;
    private readonly AmqpEndpointOptions _options;

    /// <summary>Bare names of standard AMQP properties — excluded from the application-properties bulk copy.</summary>
    private static readonly HashSet<string> StandardPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MessageId", "CorrelationId", "ReplyTo", "ContentType", "ContentEncoding", "Subject", "To",
        "UserId", "GroupId", "GroupSequence", "ReplyToGroupId", "CreationTime", "AbsoluteExpiryTime",
        "Durable", "Priority", "Ttl"
    };

    private SenderLink? _sender;

    // ── Local transactions (localTransactions=true) ──
    // The client's own transaction support goes through System.Transactions, and its discharge never got an answer from
    // the broker (docs/TRANSACTED_BATCH_COMMIT_2026_09_19.md); driven by hand on the public protocol types — a coordinator
    // link, Declare, sends carrying TransactionalState, Discharge — the same broker answers at once. One transaction at a
    // time per producer: they take turns.
    private readonly SemaphoreSlim _transactionTurn = new(1, 1);
    private readonly string _batchKey = $"amqp-tx-{Guid.NewGuid():N}";
    private SenderLink? _controller;

    // ── RPC infrastructure ──
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IMessage>> _pendingResponses = new();
    private ReceiverLink? _replyReceiver;
    private string? _replyAddress;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private volatile bool _rpcSetup;
    private CancellationTokenSource? _rpcCts;
    private Task? _rpcTask;

    /// <summary>Creates an AMQP producer.</summary>
    public AmqpProducer(AmqpEndpoint endpoint, AmqpEndpointOptions options)
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
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"amqp:{_endpoint.Address}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _sender = await _endpoint.CreateSenderLinkAsync(ct: ct).ConfigureAwait(false);

        try
        {
            if (_options.ReplyTo)
                await SetupReplyReceiverAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "AMQP producer ConnectAsync failed for address {Address}", _endpoint.Address);
            if (_sender is { IsClosed: false })
            {
                try { await _sender.CloseAsync().ConfigureAwait(false); }
                catch (Exception closeEx) { Logger?.LogWarning(closeEx, "AMQP: error closing sender during ConnectAsync cleanup"); }
            }
            _sender = null;
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task DisconnectAsync(CancellationToken ct)
    {
        foreach (var pending in _pendingResponses)
            pending.Value.TrySetCanceled(ct);
        _pendingResponses.Clear();

        _rpcCts?.Cancel();

        if (_rpcTask != null)
        {
            try { await _rpcTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _rpcTask = null;
        }

        _rpcCts?.Dispose();
        _rpcCts = null;

        if (_replyReceiver is { IsClosed: false })
        {
            try { await _replyReceiver.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogDebug(ex, "AMQP: error closing reply receiver during stop"); }
        }
        _replyReceiver = null;
        _replyAddress = null;
        _rpcSetup = false;

        if (_controller is { IsClosed: false })
        {
            try { await _controller.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogDebug(ex, "AMQP: error closing the transaction controller during stop"); }
        }
        _controller = null;

        if (_sender is { IsClosed: false })
        {
            try { await _sender.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogDebug(ex, "AMQP: error closing sender during stop"); }
        }
        _sender = null;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        EnsureSender();

        using var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.Address} publish", ActivityKind.Producer);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "amqp");
            activity.SetTag("messaging.operation", "publish");
            activity.SetTag("messaging.destination.name", _endpoint.Address);
        }

        var msg = PrepareMessage(exchange);

        if (activity is { IsAllDataRequested: true } && msg.Properties?.Subject is { } subject)
            activity.SetTag("messaging.amqp.subject", subject);
        InjectTraceContext(activity, msg);

        if (!_options.ReplyTo && TransactedActions.Defers(exchange, _options.Transacted))
        {
            ProcessTransactional(exchange, msg);
            return;
        }

        // The AMQP client enlists a send in the ambient System.Transactions transaction on its own. A send that goes out
        // at once (transacted=false, request-reply, or no enclosing block) must not: it would wait for the block's commit,
        // and next to a database in the same block it would escalate the transaction to a distributed one.
        using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            if (_options.ReplyTo)
                await ProcessRpcAsync(exchange, msg, ct).ConfigureAwait(false);
            else
                await ProcessImmediateAsync(msg, ct).ConfigureAwait(false);
        }
    }

    // ── Immediate send ──

    private async Task ProcessImmediateAsync(AmqpMessage msg, CancellationToken ct)
    {
        await _sender!.SendAsync(msg).ConfigureAwait(false);

        // Wire-level bytes are the connector's to record: the core's ToProcessor counts
        // MessagesOut/Errors for a routed producer but has no idea of payload sizes.
        var payload = PayloadSize(msg);
        if (payload > 0)
            _endpoint.RecordBytesOut(payload);

        Logger?.LogDebug("AMQP immediate send: address={Address}", _endpoint.Address);
    }

    /// <summary>
    /// Payload size of an outgoing message. Checked against the BODY SECTION: AMQPNetLite's
    /// <c>Message.Body</c> getter unwraps a <c>Data</c> section to its byte[] and our own
    /// <see cref="PrepareMessage"/> builds an <c>AmqpValue</c> - so the old
    /// <c>msg.Body is Data</c> pattern was never true and BytesOut stayed at zero forever
    /// (ревью дуги, H4).
    /// </summary>
    private static int PayloadSize(AmqpMessage msg) => msg.BodySection switch
    {
        Data { Binary.Length: > 0 } d => d.Binary.Length,
        AmqpValue { Value: byte[] b } => b.Length,
        AmqpValue { Value: string s } => System.Text.Encoding.UTF8.GetByteCount(s),
        _ => 0,
    };

    // ── Transactional deferred send ──

    private void ProcessTransactional(IExchange exchange, AmqpMessage msg)
    {
        var cloned = CloneMessage(msg);
        if (_options.LocalTransactions)
        {
            // The block's sends through this producer commit in one AMQP local transaction when the block commits.
            TransactedActions.JoinBatch(exchange, _batchKey, () => new AmqpTransactionBatch(this), ProducerName).Add(cloned);
            return;
        }

        var action = new AmqpSendAction(_sender!, cloned, _endpoint.Address, Logger);
        TransactedActions.Register(exchange, $"amqp-send-{Guid.NewGuid():N}", action, ProducerName);
    }

    // ── Local transaction ──

    /// <summary>
    /// Sends <paramref name="messages"/> in one AMQP local transaction: declare, send each carrying the transaction id,
    /// discharge. A send the broker does not accept, or any failure before the discharge, discharges the transaction as
    /// failed, so none of them is delivered, and propagates.
    /// </summary>
    internal async Task CommitTransactionAsync(IReadOnlyList<AmqpMessage> messages, CancellationToken ct)
    {
        await _transactionTurn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sender = _sender is { IsClosed: false } live
                ? live
                : throw new InvalidOperationException(
                    $"'{ProducerName}' is stopped or its link is closed: the transaction cannot be committed.");
            var controller = Controller(sender);
            var txnId = await DeclareAsync(controller).ConfigureAwait(false);

            try
            {
                foreach (var message in messages)
                {
                    var outcome = await SendAsync(sender, message, new TransactionalState { TxnId = txnId }).ConfigureAwait(false);
                    if (!IsAccepted(outcome))
                        throw new InvalidOperationException(
                            $"AMQP broker did not accept a send to '{_endpoint.Address}' in the transaction: {Describe(outcome)}.");
                }

                var committed = await SendAsync(controller, Control(new Discharge { TxnId = txnId, Fail = false }), null)
                    .ConfigureAwait(false);
                if (!IsAccepted(committed))
                    throw new InvalidOperationException(
                        $"AMQP broker did not commit the transaction on '{_endpoint.Address}': {Describe(committed)}.");
            }
            catch (Exception failure)
            {
                await DischargeAsFailedAsync(controller, txnId, failure).ConfigureAwait(false);
                throw;
            }

            foreach (var message in messages)
            {
                var payload = PayloadSize(message);
                if (payload > 0)
                    _endpoint.RecordBytesOut(payload);
            }
            Logger?.LogDebug("AMQP local transaction committed: address={Address}, messages={Count}",
                _endpoint.Address, messages.Count);
        }
        finally
        {
            _transactionTurn.Release();
        }
    }

    /// <summary>The link to the broker's transaction coordinator, on the session the sends go through.</summary>
    private SenderLink Controller(SenderLink sender)
    {
        if (_controller is { IsClosed: false } controller && ReferenceEquals(controller.Session, sender.Session))
            return controller;

        _controller = new SenderLink(sender.Session, $"txn-controller-{_endpoint.Address}-{Guid.NewGuid():N}", new Attach
        {
            Source = new Source(),
            Target = new Coordinator
            {
                Capabilities = [TxnCapabilities.LocalTransactions],
            },
        }, null);
        return _controller;
    }

    private async Task<byte[]> DeclareAsync(SenderLink controller)
    {
        Outcome declared;
        try
        {
            declared = await SendAsync(controller, Control(new Declare()), null).ConfigureAwait(false);
        }
        catch (Exception ex) when (controller.IsClosed)
        {
            throw new InvalidOperationException(
                $"AMQP broker for '{_endpoint.Address}' refused the transaction coordinator ({controller.Error?.Description ?? ex.Message}): " +
                "localTransactions needs a broker with AMQP local transactions (ActiveMQ Artemis, Qpid, Azure Service Bus).", ex);
        }

        return declared is Declared { TxnId: { } txnId }
            ? txnId
            : throw new InvalidOperationException(
                $"AMQP broker for '{_endpoint.Address}' did not declare a transaction: {Describe(declared)}.");
    }

    private async Task DischargeAsFailedAsync(SenderLink controller, byte[] txnId, Exception failure)
    {
        try
        {
            await SendAsync(controller, Control(new Discharge { TxnId = txnId, Fail = true }), null).ConfigureAwait(false);
        }
        catch (Exception dischargeFailure)
        {
            // Nothing was committed either way: the broker drops an undischarged transaction with the link.
            Logger?.LogWarning(dischargeFailure,
                "AMQP: discharging the failed transaction on {Address} failed too (the original failure: {Failure})",
                _endpoint.Address, failure.Message);
        }
    }

    /// <summary>
    /// One step of the transaction, bounded by <c>timeout</c>: a broker that withholds credit (Artemis on a full address)
    /// or never settles fails the step instead of holding the block's commit.
    /// </summary>
    private Task<Outcome> SendAsync(SenderLink link, AmqpMessage message, DeliveryState? state)
    {
        var outcome = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Send(message, state, (_, _, result, _) => outcome.TrySetResult(result), null);
        return outcome.Task.WaitAsync(TimeSpan.FromSeconds(_options.Timeout));
    }

    private static AmqpMessage Control(object body) => new() { BodySection = new AmqpValue { Value = body } };

    /// <summary>The client hands the outcome of a transactional send over unwrapped from its TransactionalState.</summary>
    private static bool IsAccepted(Outcome? outcome) => outcome is Accepted;

    private static string Describe(Outcome? outcome) => outcome switch
    {
        Rejected { Error: { } error } => $"rejected ({error.Condition}: {error.Description})",
        null => "no outcome",
        _ => outcome.ToString() ?? outcome.GetType().Name,
    };

    // ── RPC (request/reply) ──

    private async Task ProcessRpcAsync(IExchange exchange, AmqpMessage msg, CancellationToken ct)
    {
        if (!_rpcSetup)
            await SetupReplyReceiverAsync(ct).ConfigureAwait(false);

        var correlationId = Guid.NewGuid().ToString();
        msg.Properties.CorrelationId = correlationId;
        msg.Properties.ReplyTo = _replyAddress;

        var tcs = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[correlationId] = tcs;

        try
        {
            await _sender!.SendAsync(msg).ConfigureAwait(false);

            Logger?.LogDebug("AMQP RPC request sent: correlationId={CorrelationId}, replyTo={ReplyTo}",
                correlationId, _replyAddress);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.Timeout));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var responseTask = tcs.Task;
            var completed = await Task.WhenAny(responseTask, Task.Delay(Timeout.Infinite, linked.Token))
                .ConfigureAwait(false);

            if (completed != responseTask)
            {
                _pendingResponses.TryRemove(correlationId, out _);
                throw new TimeoutException(
                    $"AMQP RPC timeout ({_options.Timeout}s): address={_endpoint.Address}");
            }

            var response = await responseTask.ConfigureAwait(false);
            exchange.Out = response;
            exchange.Pattern = ExchangePattern.InOut;

            Logger?.LogDebug("AMQP RPC response received: correlationId={CorrelationId}", correlationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogError(ex, "AMQP RPC timeout/error: address={Address}, correlationId={CorrelationId}",
                _endpoint.Address, correlationId);
            _pendingResponses.TryRemove(correlationId, out _);
            throw;
        }
    }

    // ── RPC reply receiver setup ──

    private async Task SetupReplyReceiverAsync(CancellationToken ct)
    {
        if (_rpcSetup) return;

        await _rpcLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rpcSetup) return;

            await _endpoint.EnsureConnectionForRpcAsync(ct).ConfigureAwait(false);

            var source = new Source { Dynamic = true };
            var addressTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _replyReceiver = new ReceiverLink(
                _endpoint.CurrentSession!,
                $"reply-{_endpoint.Address}-{Guid.NewGuid():N}",
                source,
                (link, attach) =>
                {
                    var remoteSource = attach.Source as Source;
                    addressTcs.TrySetResult(remoteSource?.Address ?? string.Empty);
                });

            _replyReceiver.SetCredit(_options.ResolvedConcurrentConsumers * 3, true);

            // Wait for broker to assign the dynamic address
            _replyAddress = await addressTcs.Task.ConfigureAwait(false);

            _rpcCts = new CancellationTokenSource();
            _rpcTask = Task.Run(() => RpcReceiveLoopAsync(_rpcCts.Token), _rpcCts.Token);

            _rpcSetup = true;
            Logger?.LogDebug("AMQP RPC reply receiver created: replyAddress={ReplyAddress}", _replyAddress);
        }
        finally
        {
            _rpcLock.Release();
        }
    }

    private async Task RpcReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _replyReceiver is { IsClosed: false })
        {
            try
            {
                var rMsg = await _replyReceiver.ReceiveAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (rMsg == null) continue;

                var cid = rMsg.Properties?.CorrelationId?.ToString();
                if (!string.IsNullOrEmpty(cid) && _pendingResponses.TryRemove(cid, out var tcs))
                {
                    try
                    {
                        var body = AmqpMessageHelper.ExtractBody(rMsg);
                        var response = new RouteMessage(body);
                        AmqpMessageHelper.CopyApplicationProperties(rMsg, response);
                        tcs.TrySetResult(response);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }

                _replyReceiver.Accept(rMsg);
            }
            catch (AmqpException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in AMQP RPC receive loop: address={Address}",
                    _endpoint.Address);
            }
        }
    }

    // ── Message preparation ──

    private AmqpMessage PrepareMessage(IExchange exchange)
    {
        var body = exchange.In.Body switch
        {
            byte[] bytes => (object)bytes,
            string str   => str,
            null         => string.Empty,
            var other    => other.ToString() ?? string.Empty
        };

        // Resolve priority: header > expression > static
        byte priority = _options.MessagePriority;
        if (exchange.In.Headers.TryGetValue(AmqpHeaders.Priority, out var prioObj))
        {
            if (prioObj is byte prioByte)
                priority = prioByte;
            else if (prioObj is int prioInt)
                priority = (byte)Math.Clamp(prioInt, 0, 9);
        }
        else if (!string.IsNullOrEmpty(_options.MessagePriorityExpression))
        {
            var resolved = _options.ResolveOption(_options.MessagePriorityExpression, exchange);
            if (resolved != null && byte.TryParse(resolved, out var exprPrio))
                priority = exprPrio;
        }

        // Resolve TTL: header > expression > static
        uint ttl = _options.MessageTtl;
        if (exchange.In.Headers.TryGetValue(AmqpHeaders.Ttl, out var ttlObj))
        {
            if (ttlObj is uint ttlUint)
                ttl = ttlUint;
            else if (ttlObj is int ttlInt && ttlInt > 0)
                ttl = (uint)ttlInt;
        }
        else if (!string.IsNullOrEmpty(_options.MessageTtlExpression))
        {
            var resolved = _options.ResolveOption(_options.MessageTtlExpression, exchange);
            if (resolved != null && uint.TryParse(resolved, out var exprTtl))
                ttl = exprTtl;
        }

        // Resolve string properties: header > expression > static
        var contentType = exchange.In.ContentType
                          ?? _options.ResolveOption(_options.ContentType, exchange);

        var subject = _options.ResolveOption(_options.Subject, exchange);
        var groupId = _options.ResolveOption(_options.GroupId, exchange);

        var msg = new AmqpMessage(body)
        {
            Header = new Header
            {
                Durable = _options.MessageDurable,
                Priority = priority,
            },
            Properties = new Properties
            {
                MessageId = Guid.NewGuid().ToString(),
                ContentType = contentType,
            }
        };

        if (ttl > 0)
            msg.Header.Ttl = ttl;

        if (!string.IsNullOrEmpty(subject))
            msg.Properties.Subject = subject;

        if (!string.IsNullOrEmpty(groupId))
            msg.Properties.GroupId = groupId;

        // Copy exchange headers → AMQP application-properties
        var appProps = new ApplicationProperties();
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (AmqpHeaders.IsRedbHeader(key) || StandardPropertyNames.Contains(key)) continue;
            if (value is null) continue;

            appProps[key] = value switch
            {
                string s => s,
                int or long or float or double or bool => value,
                _ => value.ToString()!
            };
        }

        if (appProps.Map.Count > 0)
            msg.ApplicationProperties = appProps;

        // ── Forward AMQP properties from headers ──────────────────────────────────
        // Bare well-known names (MessageId/CorrelationId/ReplyTo/Subject/GroupId/To/…) — same
        // convention as the RabbitMQ component; redbAmqp.* is also accepted for back-compat.
        // Header wins over option/default. Typed values (DateTime/uint/bool) round-trip from the
        // consumer as-is; string forms are parsed as a fallback.
        if (TryHdr(exchange, AmqpHeaders.MessageId, out var midH))       msg.Properties.MessageId = midH;
        if (TryHdr(exchange, AmqpHeaders.CorrelationId, out var cidH))   msg.Properties.CorrelationId = cidH;
        if (TryHdr(exchange, AmqpHeaders.ReplyTo, out var rtoH))         msg.Properties.ReplyTo = rtoH;
        if (TryHdr(exchange, AmqpHeaders.Subject, out var subjH))        msg.Properties.Subject = subjH;
        if (TryHdr(exchange, AmqpHeaders.GroupId, out var gidH))         msg.Properties.GroupId = gidH;
        if (TryHdr(exchange, AmqpHeaders.To, out var toH))              msg.Properties.To = toH;
        if (TryHdr(exchange, AmqpHeaders.ContentEncoding, out var ceH))  msg.Properties.ContentEncoding = ceH;
        if (TryHdr(exchange, AmqpHeaders.ReplyToGroupId, out var rtgH))  msg.Properties.ReplyToGroupId = rtgH;
        if (TryHdr(exchange, AmqpHeaders.UserId, out var uidH))          msg.Properties.UserId = Encoding.UTF8.GetBytes(uidH);

        if (TryRaw(exchange, AmqpHeaders.GroupSequence, out var gsRaw))
        {
            if (gsRaw is uint gsu) msg.Properties.GroupSequence = gsu;
            else if (uint.TryParse(gsRaw.ToString(), out var gsp)) msg.Properties.GroupSequence = gsp;
        }
        if (TryRaw(exchange, AmqpHeaders.CreationTime, out var ctRaw))
        {
            if (ctRaw is DateTime ctd) msg.Properties.CreationTime = ctd;
            else if (DateTime.TryParse(ctRaw.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var ctp)) msg.Properties.CreationTime = ctp;
        }
        if (TryRaw(exchange, AmqpHeaders.AbsoluteExpiryTime, out var aeRaw))
        {
            if (aeRaw is DateTime aed) msg.Properties.AbsoluteExpiryTime = aed;
            else if (DateTime.TryParse(aeRaw.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var aep)) msg.Properties.AbsoluteExpiryTime = aep;
        }
        if (TryRaw(exchange, AmqpHeaders.Durable, out var durRaw))
        {
            if (durRaw is bool durb) msg.Header.Durable = durb;
            else if (bool.TryParse(durRaw.ToString(), out var durp)) msg.Header.Durable = durp;
        }

        return msg;
    }

    /// <summary>Reads a header by bare name, then <c>redbAmqp.&lt;name&gt;</c> for back-compat; true when found non-empty (as string).</summary>
    private static bool TryHdr(IExchange exchange, string name, out string value)
    {
        if ((exchange.In.Headers.TryGetValue(name, out var v) || exchange.In.Headers.TryGetValue(AmqpHeaders.Prefix + name, out v)) && v is not null)
        {
            value = v.ToString() ?? string.Empty;
            return !string.IsNullOrEmpty(value);
        }
        value = string.Empty;
        return false;
    }

    /// <summary>Reads a header's raw value by bare name, then <c>redbAmqp.&lt;name&gt;</c> for back-compat.</summary>
    private static bool TryRaw(IExchange exchange, string name, out object value)
    {
        if ((exchange.In.Headers.TryGetValue(name, out var v) || exchange.In.Headers.TryGetValue(AmqpHeaders.Prefix + name, out v)) && v is not null)
        {
            value = v;
            return true;
        }
        value = null!;
        return false;
    }

    // ── Trace context ──

    private static void InjectTraceContext(Activity? activity, AmqpMessage msg)
    {
        if (activity is null) return;

        msg.ApplicationProperties ??= new ApplicationProperties();

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, msg.ApplicationProperties.Map, static (carrier, key, value) =>
        {
            if (carrier is global::Amqp.Types.Map map && !string.IsNullOrEmpty(value))
                map[key] = value;
        });
    }

    // ── Helpers ──

    private void EnsureSender()
    {
        if (_sender is null or { IsClosed: true })
            throw new InvalidOperationException("AMQP sender link is not available. The producer might need restart.");
    }

    private static AmqpMessage CloneMessage(AmqpMessage source)
    {
        var buffer = source.Encode();
        return AmqpMessage.Decode(buffer);
    }

}

/// <summary>Deferred AMQP send action.</summary>
internal sealed class AmqpSendAction : ITransactedAction
{
    private readonly SenderLink _sender;
    private readonly AmqpMessage _msg;
    private readonly string _address;
    private readonly ILogger? _logger;

    public AmqpSendAction(SenderLink sender, AmqpMessage msg, string address, ILogger? logger)
    {
        _sender = sender;
        _msg = msg;
        _address = address;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Commit(CancellationToken ct = default)
    {
        await _sender.SendAsync(_msg).ConfigureAwait(false);
        _logger?.LogDebug("AMQP transactional send committed: address={Address}", _address);
    }

    /// <inheritdoc />
    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("AMQP transactional send rolled back: address={Address}", _address);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The sends an AMQP producer with <c>localTransactions</c> deferred in one <c>.Transacted()</c> block, committed in one
/// AMQP local transaction when the block commits.
/// </summary>
internal sealed class AmqpTransactionBatch : ITransactedAction
{
    private readonly AmqpProducer _producer;
    private readonly ConcurrentQueue<AmqpMessage> _messages = new();

    public AmqpTransactionBatch(AmqpProducer producer) => _producer = producer;

    public void Add(AmqpMessage message) => _messages.Enqueue(message);

    public Task Commit(CancellationToken ct = default) => _producer.CommitTransactionAsync(_messages.ToArray(), ct);

    // The block rolled back before the transaction was declared: nothing was sent.
    public Task Rollback(CancellationToken ct = default) => Task.CompletedTask;
}
