using System.Collections.Concurrent;
using Amqp;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Amqp;

/// <summary>
/// AMQP 1.0 transport component for redb.Route.
/// Scheme: <c>amqp</c>.
/// <para>
/// URI format: <c>amqp://address-name?host=broker&amp;port=5672&amp;user=admin&amp;password=admin</c>
/// </para>
/// <para>
/// Supports any AMQP 1.0 broker: ActiveMQ Artemis, ActiveMQ Classic, Azure Service Bus,
/// Amazon MQ, Qpid, and more. Pure AMQP 1.0 — no JMS wrappers.
/// </para>
/// <para>
/// Owns a process-wide pool of <see cref="Connection"/> instances, shared between endpoints by
/// the same key model used for RabbitMQ:
/// <list type="bullet">
///   <item><c>connectionFactory=name</c> on the URI \u2192 key <c>"factory:{name}"</c>. Two factories
///     with identical inline parameters but different names produce two distinct connections.</item>
///   <item>Otherwise \u2192 key <c>"inline:{host}:{port}/{vhost}@{user}#{ssl}"</c>.</item>
/// </list>
/// Sessions are owned per-endpoint (Session is a lightweight AMQP framing context). Connections
/// survive endpoint Stop/Start cycles and are released on <see cref="RouteContext.DisposeAsync"/>.
/// </para>
/// </summary>
public sealed class AmqpComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Connection>>> _connections = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override string Scheme => "amqp";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new AmqpEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new AmqpEndpoint(uri, this, options);
    }

    /// <summary>
    /// Returns the shared <see cref="Connection"/> for the given endpoint, creating it on first
    /// request. Self-healing: if the cached connection has been closed (broker reset, network),
    /// it is evicted and recreated on the next call.
    /// </summary>
    internal async Task<Connection> GetOrCreateConnectionAsync(AmqpEndpoint endpoint, CancellationToken ct)
    {
        var key = ResolveConnectionKey(endpoint.EndpointOptions);
        var lazy = _connections.GetOrAdd(key, k =>
            new Lazy<Task<Connection>>(() => CreateConnectionAsync(k, endpoint, ct)));

        Connection conn;
        try
        {
            conn = await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<Connection>>>>)_connections)
                .Remove(new KeyValuePair<string, Lazy<Task<Connection>>>(key, lazy));
            throw;
        }

        if (conn.IsClosed)
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<Connection>>>>)_connections)
                .Remove(new KeyValuePair<string, Lazy<Task<Connection>>>(key, lazy));

            return await GetOrCreateConnectionAsync(endpoint, ct).ConfigureAwait(false);
        }

        return conn;
    }

    /// <summary>Number of currently pooled connections (for diagnostics and tests).</summary>
    internal int PooledConnectionCount => _connections.Count;

    /// <summary>Returns a snapshot of the open pooled connections (for diagnostics and tests).</summary>
    internal async Task<IReadOnlyList<Connection>> GetPooledConnectionsAsync()
    {
        var list = new List<Connection>();
        foreach (var lazy in _connections.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try { list.Add(await lazy.Value.ConfigureAwait(false)); }
            catch { /* skip failed entries */ }
        }
        return list;
    }

    /// <summary>Computes the pool key for the given endpoint options.</summary>
    internal static string ResolveConnectionKey(AmqpEndpointOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.ConnectionFactory))
            return "factory:" + opts.ConnectionFactory;

        return string.Concat(
            "inline:",
            opts.Host, ":", opts.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "/", opts.VirtualHost,
            "@", opts.User,
            opts.Ssl ? "#ssl" : string.Empty);
    }

    private async Task<Connection> CreateConnectionAsync(string key, AmqpEndpoint endpoint, CancellationToken ct)
    {
        var options = endpoint.EndpointOptions;
        var factory = BuildConnectionFactory(options);
        var address = BuildAddress(options);

        var connection = await factory.CreateAsync(address).ConfigureAwait(false);
        endpoint.AttachConnectionEvents(connection);

        Logger?.LogInformation("AMQP connection opened [{Key}]: host={Host}, vHost={VHost}",
            key, options.Host, options.VirtualHost);

        return connection;
    }

    private ConnectionFactory BuildConnectionFactory(AmqpEndpointOptions options)
    {
        if (!string.IsNullOrEmpty(options.ConnectionFactory))
        {
            var registryFactory = Context?.GetFromRegistry<AmqpConnectionFactory>(options.ConnectionFactory);
            if (registryFactory is not null)
            {
                Logger?.LogDebug("AMQP: using ConnectionFactory '{Name}' from registry", options.ConnectionFactory);
                return registryFactory.Build();
            }

            Logger?.LogWarning(
                "AMQP: ConnectionFactory '{Name}' not found in registry — falling back to inline URI parameters",
                options.ConnectionFactory);
        }

        var cf = new AmqpConnectionFactory
        {
            Host = options.Host,
            Port = options.Port,
            User = options.User,
            Password = options.Password,
            ContainerId = options.ContainerId,
            VirtualHost = options.VirtualHost,
            Ssl = options.Ssl,
        };

        return cf.Build();
    }

    private static Address BuildAddress(AmqpEndpointOptions options)
    {
        var scheme = options.Ssl ? "amqps" : "amqp";
        var hosts = options.Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var primaryHost = hosts.Length > 0 ? hosts[0] : "localhost";
        return new Address(primaryHost, options.Port, options.User, options.Password, "/", scheme);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        foreach (var (key, lazy) in _connections.ToArray())
        {
            if (!lazy.IsValueCreated) continue;

            Connection? conn = null;
            try { conn = await lazy.Value.ConfigureAwait(false); }
            catch { /* failed initialisation */ }

            if (conn is null) continue;

            try { if (!conn.IsClosed) await conn.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogWarning(ex, "AMQP: error closing pooled connection [{Key}]", key); }
        }

        _connections.Clear();
        Logger?.LogInformation("AMQP component disposed: connection pool drained");
    }
}
