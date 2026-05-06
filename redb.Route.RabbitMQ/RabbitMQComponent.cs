using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.RabbitMQ;

/// <summary>
/// RabbitMQ transport component for redb.Route.
/// Scheme: <c>rabbitmq</c>.
/// <para>
/// URI format: <c>rabbitmq://queue-name?host=localhost&amp;exchange=my-exchange&amp;routingKey=key</c>
/// </para>
/// <para>
/// Owns a process-wide pool of <see cref="IConnection"/> instances, shared between endpoints
/// according to a deterministic key:
/// <list type="bullet">
///   <item>If <c>connectionFactory=name</c> is set on the URI — key is <c>"factory:{name}"</c>.
///     Two factories with identical parameters but different names produce two distinct
///     connections (the factory name is the connection identity).</item>
///   <item>Otherwise — key is <c>"inline:{host}:{port}/{vhost}@{user}#{ssl}"</c>. Endpoints
///     with identical inline connection parameters share one connection automatically.</item>
/// </list>
/// Connections are created lazily on first endpoint use and released only when the component
/// itself is disposed by <see cref="RouteContext.DisposeAsync"/>. Channels are cheap and remain
/// owned by individual producers/consumers; <c>endpoint.Stop()</c> closes channels but never
/// pooled connections (so connections survive Stop/Start cycles).
/// </para>
/// </summary>
public sealed partial class RabbitMQComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IConnection>>> _connections = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override string Scheme => "rabbitmq";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new RabbitMQEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new RabbitMQEndpoint(uri, this, options);
    }

    /// <summary>
    /// Returns the shared <see cref="IConnection"/> for the given endpoint, creating it on first
    /// request. Self-healing: if the cached connection has been closed (auto-recovery gave up,
    /// broker permanently rejected, etc.) it is evicted and recreated on the next call.
    /// </summary>
    internal async Task<IConnection> GetOrCreateConnectionAsync(RabbitMQEndpoint endpoint, CancellationToken ct)
    {
        var key = ResolveConnectionKey(endpoint.EndpointOptions);
        var lazy = _connections.GetOrAdd(key, k =>
            new Lazy<Task<IConnection>>(() => CreateConnectionAsync(k, endpoint, ct)));

        IConnection conn;
        try
        {
            conn = await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Failed initialization must not poison the slot — evict so the next caller retries.
            ((ICollection<KeyValuePair<string, Lazy<Task<IConnection>>>>)_connections)
                .Remove(new KeyValuePair<string, Lazy<Task<IConnection>>>(key, lazy));
            throw;
        }

        if (!conn.IsOpen)
        {
            // Broker terminated the connection beyond auto-recovery's reach.
            // Evict only if our entry is still the cached one (avoid racing another caller).
            ((ICollection<KeyValuePair<string, Lazy<Task<IConnection>>>>)_connections)
                .Remove(new KeyValuePair<string, Lazy<Task<IConnection>>>(key, lazy));

            try { await conn.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogDebug(ex, "RabbitMQ: error disposing dead connection [{Key}]", key); }

            return await GetOrCreateConnectionAsync(endpoint, ct).ConfigureAwait(false);
        }

        return conn;
    }

    /// <summary>Number of currently pooled connections (for diagnostics and tests).</summary>
    internal int PooledConnectionCount => _connections.Count;

    /// <summary>Returns a snapshot of the open pooled connections (for diagnostics and tests).</summary>
    internal async Task<IReadOnlyList<global::RabbitMQ.Client.IConnection>> GetPooledConnectionsAsync()
    {
        var list = new List<global::RabbitMQ.Client.IConnection>();
        foreach (var lazy in _connections.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try { list.Add(await lazy.Value.ConfigureAwait(false)); }
            catch { /* skip failed entries */ }
        }
        return list;
    }

    /// <summary>
    /// Computes the pool key for the given endpoint options. See class XML doc for the rule set.
    /// </summary>
    internal static string ResolveConnectionKey(RabbitMQEndpointOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.ConnectionFactory))
            return "factory:" + opts.ConnectionFactory;

        return string.Concat(
            "inline:",
            opts.Host, ":", opts.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "/", opts.VirtualHost,
            "@", opts.Username,
            opts.Ssl ? "#ssl" : string.Empty);
    }

    /// <summary>
    /// Builds the underlying <see cref="ConnectionFactory"/> and opens the physical connection.
    /// Honours <see cref="RabbitMQEndpointOptions.ConnectionFactory"/> registry lookup with
    /// fallback to inline parameters; supports comma-separated host lists for cluster mode.
    /// </summary>
    private async Task<IConnection> CreateConnectionAsync(string key, RabbitMQEndpoint endpoint, CancellationToken ct)
    {
        var options = endpoint.EndpointOptions;
        var factory = BuildConnectionFactory(options);
        var hosts = ParseEndpoints(options);

        var connection = hosts.Count > 1
            ? await factory.CreateConnectionAsync(hosts, ct).ConfigureAwait(false)
            : await factory.CreateConnectionAsync(ct).ConfigureAwait(false);

        endpoint.AttachConnectionLifecycleHandlers(connection);

        Logger?.LogInformation(
            "RabbitMQ connection opened [{Key}]: host={Host}, vHost={VHost}, user={User}",
            key, options.Host, options.VirtualHost, options.Username);

        return connection;
    }

    private ConnectionFactory BuildConnectionFactory(RabbitMQEndpointOptions options)
    {
        // Named factory from registry — its settings are authoritative; inline overrides are not
        // applied (matches the historical contract of RabbitMQConnectionFactory.Build()).
        if (!string.IsNullOrEmpty(options.ConnectionFactory))
        {
            var registryFactory = Context?.GetFromRegistry<RabbitMQConnectionFactory>(options.ConnectionFactory);
            if (registryFactory is not null)
            {
                Logger?.LogDebug("RabbitMQ: using ConnectionFactory '{Name}' from registry", options.ConnectionFactory);
                return registryFactory.Build();
            }

            Logger?.LogWarning(
                "RabbitMQ: ConnectionFactory '{Name}' not found in registry — falling back to inline URI parameters",
                options.ConnectionFactory);
        }

        var hostList = options.Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var primaryHost = hostList.Length > 0 ? hostList[0] : "localhost";

        return new ConnectionFactory
        {
            HostName = primaryHost,
            Port = options.Port,
            UserName = options.Username,
            Password = options.Password,
            VirtualHost = options.VirtualHost,
            AutomaticRecoveryEnabled = options.AutomaticRecovery,
            TopologyRecoveryEnabled = options.TopologyRecoveryEnabled,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(options.RecoveryInterval),
            RequestedHeartbeat = TimeSpan.FromSeconds(options.Heartbeat),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(options.ConnectionTimeout),
            SocketReadTimeout = TimeSpan.FromSeconds(options.SocketReadTimeout),
            SocketWriteTimeout = TimeSpan.FromSeconds(options.SocketWriteTimeout),
            ContinuationTimeout = TimeSpan.FromSeconds(options.ContinuationTimeout),
            ConsumerDispatchConcurrency = options.ConsumerDispatchConcurrency,
            ClientProvidedName = options.ClientName,
            Ssl = options.Ssl
                ? new SslOption
                {
                    Enabled = true,
                    ServerName = options.SslServerName,
                    CertPath = options.SslCertPath,
                    CertPassphrase = options.SslCertPassphrase,
                }
                : new SslOption(),
        };
    }

    private static List<AmqpTcpEndpoint> ParseEndpoints(RabbitMQEndpointOptions options)
    {
        return options.Host
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => new AmqpTcpEndpoint(h, options.Port))
            .ToList();
    }

    /// <summary>
    /// Closes every pooled connection. Called by <see cref="RouteContext.DisposeAsync"/>;
    /// individual endpoint <c>Stop</c> calls do not close pooled connections (channels only),
    /// so connections survive Stop/Start cycles intentionally.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        foreach (var (key, lazy) in _connections.ToArray())
        {
            if (!lazy.IsValueCreated) continue;

            IConnection? conn = null;
            try { conn = await lazy.Value.ConfigureAwait(false); }
            catch { /* failed initialization — nothing to close */ }

            if (conn is null) continue;

            try { if (conn.IsOpen) await conn.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogWarning(ex, "RabbitMQ: error closing pooled connection [{Key}]", key); }

            try { await conn.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogWarning(ex, "RabbitMQ: error disposing pooled connection [{Key}]", key); }
        }

        _connections.Clear();
        Logger?.LogInformation("RabbitMQ component disposed: connection pool drained");
    }
}

