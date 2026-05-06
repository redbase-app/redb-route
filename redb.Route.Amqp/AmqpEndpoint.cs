using Amqp;
using Amqp.Framing;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using AmqpSymbol = global::Amqp.Types.Symbol;

namespace redb.Route.Amqp;

/// <summary>
/// AMQP 1.0 endpoint. Manages a shared <see cref="Connection"/> and <see cref="Session"/>,
/// creates producer/consumer links on demand.
/// <para>
/// Supports all AMQP 1.0 brokers: ActiveMQ Artemis, Classic, Azure Service Bus, Amazon MQ, Qpid.
/// </para>
/// </summary>
public sealed class AmqpEndpoint : EndpointBase<AmqpEndpointOptions>
{
    private readonly AmqpComponent _component;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private Session? _session;
    private bool _closed;

    /// <summary>Logger from the parent component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>AMQP address (node name) extracted from the URI path.</summary>
    public string Address { get; }

    /// <summary>Creates an AMQP endpoint.</summary>
    public AmqpEndpoint(EndpointUri uri, AmqpComponent component, AmqpEndpointOptions options)
        : base(uri, component, options)
    {
        _component = component;
        Logger = component.Logger;
        Address = uri.Path;
    }

    /// <summary>Typed access to options.</summary>
    internal new AmqpEndpointOptions Options => base.Options;

    /// <summary>Typed options exposed for the component pool.</summary>
    internal AmqpEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new AmqpProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor) => new AmqpConsumer(this, processor, Options);

    /// <summary>
    /// Creates a sender link on the shared session. Thread-safe.
    /// </summary>
    internal async Task<SenderLink> CreateSenderLinkAsync(string? name = null, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct).ConfigureAwait(false);

        var linkName = name ?? $"sender-{Address}-{Guid.NewGuid():N}";

        var target = new Target
        {
            Address = Address,
            Durable = Options.Durable,
        };

        var caps = Options.ResolveCapabilities();
        if (caps.Length > 0)
            target.Capabilities = caps.Select(c => new AmqpSymbol(c)).ToArray();

        var attach = new Attach
        {
            Target = target,
            SndSettleMode = (SenderSettleMode)Options.SenderSettleMode,
            RcvSettleMode = (ReceiverSettleMode)Options.ReceiverSettleMode,
        };

        var sender = new SenderLink(_session!, linkName, attach, null);

        Logger?.LogDebug("AMQP sender link created: address={Address}, name={Name}", Address, linkName);
        return sender;
    }

    /// <summary>
    /// Creates a receiver link on the shared session. Thread-safe.
    /// </summary>
    internal async Task<ReceiverLink> CreateReceiverLinkAsync(string? name = null, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct).ConfigureAwait(false);

        var linkName = name ?? $"receiver-{Address}-{Guid.NewGuid():N}";

        var source = new Source
        {
            Address = Address,
            Durable = Options.Durable,
        };

        var caps = Options.ResolveCapabilities();
        if (caps.Length > 0)
            source.Capabilities = caps.Select(c => new AmqpSymbol(c)).ToArray();

        if (!string.IsNullOrEmpty(Options.DistributionMode))
            source.DistributionMode = new AmqpSymbol(Options.DistributionMode);

        if (!string.IsNullOrEmpty(Options.FilterSelector))
        {
            source.FilterSet = new global::Amqp.Types.Map
            {
                {
                    new AmqpSymbol("apache.org:selector-filter:string"),
                    new global::Amqp.Types.DescribedValue(
                        new AmqpSymbol("apache.org:selector-filter:string"),
                        Options.FilterSelector)
                }
            };
        }

        var attach = new Attach
        {
            Source = source,
            SndSettleMode = (SenderSettleMode)Options.SenderSettleMode,
            RcvSettleMode = (ReceiverSettleMode)Options.ReceiverSettleMode,
        };

        var receiver = new ReceiverLink(_session!, linkName, attach, null);
        receiver.SetCredit(Options.Credit, true);

        Logger?.LogDebug("AMQP receiver link created: address={Address}, name={Name}, credit={Credit}",
            Address, linkName, Options.Credit);
        return receiver;
    }

    /// <summary>Gets the current session (for transacted operations).</summary>
    internal Session? CurrentSession => _session;

    /// <summary>Ensures connection is available (used by producer for RPC link setup).</summary>
    internal async Task EnsureConnectionForRpcAsync(CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the per-endpoint <see cref="Session"/> is open on a pooled connection. Thread-safe.
    /// The <see cref="Connection"/> is owned by <see cref="AmqpComponent"/>; this endpoint owns
    /// only the session. On Stop the session is closed but the pooled connection survives.
    /// </summary>
    private async Task EnsureConnectionAsync(CancellationToken ct)
    {
        if (_session is { IsClosed: false })
            return;

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is { IsClosed: false })
                return;

            var connection = await _component.GetOrCreateConnectionAsync(this, ct).ConfigureAwait(false);
            _session = new Session(connection);

            Logger?.LogInformation("AMQP session opened on pooled connection: host={Host}, address={Address}",
                Options.Host, Address);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Subscribes diagnostic logging to connection-level events. Invoked by
    /// <see cref="AmqpComponent"/> exactly once per pooled connection.
    /// </summary>
    internal void AttachConnectionEvents(Connection connection)
    {
        connection.Closed += OnConnectionClosed;
    }

    private void OnConnectionClosed(IAmqpObject sender, global::Amqp.Framing.Error error)
    {
        if (error != null)
        {
            Logger?.LogWarning("AMQP connection closed with error: {Condition} — {Description}",
                error.Condition, error.Description);
        }
        else
        {
            Logger?.LogInformation("AMQP connection closed gracefully");
        }
    }

    /// <inheritdoc />
    public override async Task Stop(CancellationToken ct = default)
    {
        if (_closed) return;
        _closed = true;

        // Close the per-endpoint Session. The pooled Connection is owned by AmqpComponent
        // and is NOT closed here — it survives Stop/Start cycles and is released only on
        // RouteContext.DisposeAsync (which calls Component.DisposeAsync).
        if (_session is { IsClosed: false })
        {
            try { await _session.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogWarning(ex, "Error closing AMQP session"); }
        }

        _session = null;

        Logger?.LogInformation("AMQP endpoint stopped: {Uri}", Uri);
    }
}
