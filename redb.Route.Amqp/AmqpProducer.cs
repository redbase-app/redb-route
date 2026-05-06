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

    private SenderLink? _sender;

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

        if (_options.ReplyTo)
            await ProcessRpcAsync(exchange, msg, ct).ConfigureAwait(false);
        else if (_options.Transacted)
            ProcessTransactional(exchange, msg);
        else
            await ProcessImmediateAsync(msg, ct).ConfigureAwait(false);
    }

    // ── Immediate send ──

    private async Task ProcessImmediateAsync(AmqpMessage msg, CancellationToken ct)
    {
        await _sender!.SendAsync(msg).ConfigureAwait(false);
        Logger?.LogDebug("AMQP immediate send: address={Address}", _endpoint.Address);
    }

    // ── Transactional deferred send ──

    private void ProcessTransactional(IExchange exchange, AmqpMessage msg)
    {
        var cloned = CloneMessage(msg);
        var action = new AmqpSendAction(_sender!, cloned, _endpoint.Address, Logger);
        RegisterTransactedAction(exchange, $"amqp-send-{Guid.NewGuid():N}", action);
    }

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

            _replyReceiver.SetCredit(_options.ConcurrentConsumers * 3, true);

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
            msg.ApplicationProperties = appProps;

        if (exchange.In.Headers.TryGetValue("CorrelationId", out var corrId) && corrId is string corrStr && !string.IsNullOrEmpty(corrStr))
            msg.Properties.CorrelationId = corrStr;

        return msg;
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
