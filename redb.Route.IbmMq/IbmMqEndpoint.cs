using IBM.WMQ;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.IbmMq;

/// <summary>
/// IBM MQ endpoint. Delegates connection ownership to <see cref="IbmMqComponent"/> via a
/// process-wide <see cref="MQQueueManager"/> pool keyed by connection identity.
/// <para>
/// The endpoint owns only its consumer/producer handles (MQQueue / MQTopic). Pooled queue
/// managers survive Stop/Start cycles and are released only when the parent
/// <see cref="RouteContext"/> is disposed.
/// </para>
/// </summary>
public sealed class IbmMqEndpoint : EndpointBase<IbmMqEndpointOptions>
{
    private readonly IbmMqComponent _component;
    private bool _closed;

    /// <summary>Logger from the parent component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Destination name (queue or topic) extracted from the URI path.</summary>
    public string Destination { get; }

    /// <summary>Creates an IBM MQ endpoint.</summary>
    public IbmMqEndpoint(EndpointUri uri, IbmMqComponent component, IbmMqEndpointOptions options)
        : base(uri, component, options)
    {
        _component = component;
        Logger = component.Logger;
        Destination = uri.Path;
    }

    /// <summary>Typed access to options.</summary>
    internal new IbmMqEndpointOptions Options => base.Options;

    /// <summary>Typed options exposed for the component pool.</summary>
    internal IbmMqEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new IbmMqProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor) => new IbmMqConsumer(this, processor, Options);

    /// <summary>
    /// Returns the shared <see cref="MQQueueManager"/> instance from the component pool,
    /// connecting on first use.
    /// </summary>
    internal Task<MQQueueManager> GetQueueManagerAsync(CancellationToken ct = default)
        => _component.GetOrCreateQueueManagerAsync(this, ct);

    /// <summary>
    /// Opens an MQ queue with the specified open options.
    /// </summary>
    internal async Task<MQQueue> OpenQueueAsync(int openOptions, CancellationToken ct = default)
    {
        var qm = await GetQueueManagerAsync(ct).ConfigureAwait(false);
        return qm.AccessQueue(Destination, openOptions);
    }

    /// <summary>
    /// Opens an MQ topic for subscribing with the specified subscribe options.
    /// </summary>
    internal async Task<MQTopic> OpenTopicAsync(string topicString, int subscribeOptions, CancellationToken ct = default)
    {
        var qm = await GetQueueManagerAsync(ct).ConfigureAwait(false);
        var subName = $"REDB.{Destination}.{Guid.NewGuid():N}";
        return qm.AccessTopic(topicString, null, MQC.MQSO_CREATE | subscribeOptions, null, subName);
    }

    /// <summary>
    /// Opens an MQ topic for publishing with the specified open options.
    /// </summary>
    internal async Task<MQTopic> OpenTopicForPublishAsync(string topicString, int openOptions, CancellationToken ct = default)
    {
        var qm = await GetQueueManagerAsync(ct).ConfigureAwait(false);
        return qm.AccessTopic(topicString, null, MQC.MQTOPIC_OPEN_AS_PUBLICATION, openOptions);
    }

    /// <inheritdoc />
    public override async Task Stop(CancellationToken ct = default)
    {
        if (_closed) return;
        _closed = true;

        // The MQQueueManager is owned by IbmMqComponent and is NOT disconnected here \u2014 it
        // survives Stop/Start cycles and is released only on RouteContext.DisposeAsync
        // (which calls Component.DisposeAsync).
        Logger?.LogInformation("IBM MQ endpoint stopped: {Uri}", Uri);

        await base.Stop(ct).ConfigureAwait(false);
    }
}
