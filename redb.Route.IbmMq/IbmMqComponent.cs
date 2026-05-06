using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using IBM.WMQ;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.IbmMq;

/// <summary>
/// IBM MQ transport component for redb.Route.
/// Scheme: <c>wmq</c>.
/// <para>
/// URI format: <c>wmq:QUEUE.NAME?host=mq-host&amp;port=1414&amp;channel=DEV.APP.SVRCONN&amp;queueManager=QM1</c>
/// </para>
/// <para>
/// Supports queues (point-to-point) and topics (pub/sub) via native IBM MQ client.
/// Full MQI access — transactions, message groups, dead-letter, RPC, SSL/TLS, W3C telemetry.
/// </para>
/// <para>
/// Owns a process-wide pool of <see cref="MQQueueManager"/> connections shared between
/// endpoints by the same identity model used for RabbitMQ and AMQP:
/// <list type="bullet">
///   <item><c>connectionFactory=name</c> on the URI \u2192 key <c>"factory:{name}"</c>. Two factories
///     with identical inline parameters but different names produce two distinct connections.</item>
///   <item>Otherwise \u2192 key <c>"inline:{host}:{port}/{queueManager}#{channel}@{user}[#ssl]"</c>.</item>
/// </list>
/// MQQueueManager is shareable across threads in the IBM .NET managed client. Per-consumer
/// MQQueue/MQTopic handles remain opened per-endpoint and are released on Stop. The pooled
/// queue managers survive Stop/Start cycles and are released only on
/// <see cref="RouteContext.DisposeAsync"/>.
/// </para>
/// </summary>
public sealed class IbmMqComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, Lazy<Task<MQQueueManager>>> _connections = new(StringComparer.Ordinal);

    static IbmMqComponent()
    {
        // IBM MQ managed client needs Windows code pages (e.g. 1252) for MQGMO_CONVERT
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <inheritdoc />
    public override string Scheme => "wmq";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new IbmMqEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new IbmMqEndpoint(uri, this, options);
    }

    /// <summary>
    /// Returns the shared <see cref="MQQueueManager"/> for the given endpoint, creating the
    /// connection on first request. Self-healing: if the cached connection is no longer
    /// connected, it is evicted and recreated on the next call.
    /// </summary>
    internal async Task<MQQueueManager> GetOrCreateQueueManagerAsync(IbmMqEndpoint endpoint, CancellationToken ct)
    {
        var key = ResolveConnectionKey(endpoint.EndpointOptions);
        var lazy = _connections.GetOrAdd(key, k =>
            new Lazy<Task<MQQueueManager>>(() => CreateQueueManagerAsync(k, endpoint, ct)));

        MQQueueManager qm;
        try
        {
            qm = await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<MQQueueManager>>>>)_connections)
                .Remove(new KeyValuePair<string, Lazy<Task<MQQueueManager>>>(key, lazy));
            throw;
        }

        if (!qm.IsConnected)
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<MQQueueManager>>>>)_connections)
                .Remove(new KeyValuePair<string, Lazy<Task<MQQueueManager>>>(key, lazy));

            return await GetOrCreateQueueManagerAsync(endpoint, ct).ConfigureAwait(false);
        }

        return qm;
    }

    /// <summary>Number of currently pooled queue managers (for diagnostics and tests).</summary>
    internal int PooledConnectionCount => _connections.Count;

    /// <summary>Returns a snapshot of the open pooled queue managers (for diagnostics and tests).</summary>
    internal async Task<IReadOnlyList<MQQueueManager>> GetPooledConnectionsAsync()
    {
        var list = new List<MQQueueManager>();
        foreach (var lazy in _connections.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try { list.Add(await lazy.Value.ConfigureAwait(false)); }
            catch { /* skip failed entries */ }
        }
        return list;
    }

    /// <summary>Computes the pool key for the given endpoint options.</summary>
    internal static string ResolveConnectionKey(IbmMqEndpointOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.ConnectionFactory))
            return "factory:" + opts.ConnectionFactory;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Concat(
            "inline:",
            opts.Host, ":", opts.Port.ToString(inv),
            "/", opts.QueueManager,
            "#", opts.Channel,
            "@", opts.User ?? string.Empty,
            string.IsNullOrEmpty(opts.SslCipherSpec) ? string.Empty : "#ssl");
    }

    private Task<MQQueueManager> CreateQueueManagerAsync(string key, IbmMqEndpoint endpoint, CancellationToken ct)
    {
        // MQQueueManager constructor is synchronous and blocking. Push it to a thread-pool
        // worker so async callers do not block on socket setup.
        return Task.Run(() =>
        {
            var options = endpoint.EndpointOptions;
            var properties = BuildConnectionProperties(options);

            var qm = new MQQueueManager(options.QueueManager, properties);

            Logger?.LogInformation(
                "IBM MQ connection opened [{Key}]: queueManager={QueueManager}, host={Host}:{Port}, channel={Channel}",
                key, options.QueueManager, options.Host, options.Port, options.Channel);

            return qm;
        }, ct);
    }

    private static Hashtable BuildConnectionProperties(IbmMqEndpointOptions options)
    {
        var props = new Hashtable
        {
            { MQC.TRANSPORT_PROPERTY, MQC.TRANSPORT_MQSERIES_MANAGED },
            { MQC.HOST_NAME_PROPERTY, options.Host },
            { MQC.PORT_PROPERTY, options.Port },
            { MQC.CHANNEL_PROPERTY, options.Channel },
            { MQC.CCSID_PROPERTY, options.CCSID },
        };

        if (!string.IsNullOrEmpty(options.User))
            props[MQC.USER_ID_PROPERTY] = options.User;

        if (!string.IsNullOrEmpty(options.Password))
            props[MQC.PASSWORD_PROPERTY] = options.Password;

        if (!string.IsNullOrEmpty(options.ClientId))
            props[MQC.APPNAME_PROPERTY] = options.ClientId;

        if (!string.IsNullOrEmpty(options.SslCipherSpec))
            props[MQC.SSL_CIPHER_SPEC_PROPERTY] = options.SslCipherSpec;

        if (!string.IsNullOrEmpty(options.SslCertLabel))
            props[MQC.SSL_CERT_STORE_PROPERTY] = options.SslCertLabel;

        if (!string.IsNullOrEmpty(options.SslPeerName))
            props[MQC.SSL_PEER_NAME_PROPERTY] = options.SslPeerName;

        if (!string.IsNullOrEmpty(options.SslKeyRepository))
            MQEnvironment.SSLKeyRepository = options.SslKeyRepository;

        return props;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        foreach (var (key, lazy) in _connections.ToArray())
        {
            if (!lazy.IsValueCreated) continue;

            MQQueueManager? qm = null;
            try { qm = await lazy.Value.ConfigureAwait(false); }
            catch { /* failed initialisation */ }

            if (qm is null) continue;

            try
            {
                if (qm.IsConnected) qm.Disconnect();
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "IBM MQ: error disconnecting pooled queue manager [{Key}]", key);
            }
        }

        _connections.Clear();
        Logger?.LogInformation("IBM MQ component disposed: connection pool drained");
    }
}
