using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.OAuth2;
using redb.Route.Abstractions;
using redb.Route.Extensions;
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
///   <item>Otherwise — key is <c>"inline:{host}:{port}/{vhost}@{user}#ssl:sni={sslServerName}:cert={sslCertPath}"</c>
///     (the TLS part only with <c>ssl=true</c>, each of its fields only when set). Endpoints
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

    /// <summary>Structured XML form (Route-XML Ф0 §7.2): <c>&lt;rabbitmq queue="orders"/&gt;</c>.</summary>
    public override string? StructuredPathSynonym => "queue";


    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new RabbitMQEndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // A connection parameter next to connectionFactory is refused by the core while binding: the options declare
        // them ([ConnectionParameter]), as a factory is the whole connection in Camel.

        options.Validate();

        return new RabbitMQEndpoint(uri, this, options);
    }

    /// <summary>
    /// Returns the shared <see cref="IConnection"/> for the given endpoint, creating it on first
    /// request. Self-healing: if the cached connection has been closed (auto-recovery gave up,
    /// broker permanently rejected, etc.) it is evicted and recreated on the next call.
    /// </summary>
    internal async Task<IConnection> GetOrCreateConnectionAsync(RabbitMQEndpoint endpoint, RabbitMQConnectionUse use, CancellationToken ct)
    {
        // publisherConnection=true: publishing gets a second connection with the same settings, so the broker's flow
        // control or an alarm on it cannot stop the consumers of the first from acking.
        var publisher = use == RabbitMQConnectionUse.Publish && endpoint.EndpointOptions.PublisherConnection;
        var key = ResolveConnectionKey(endpoint.EndpointOptions) + (publisher ? PublisherKeySuffix : string.Empty);
        var lazy = _connections.GetOrAdd(key, k =>
            new Lazy<Task<IConnection>>(() => CreateConnectionAsync(k, endpoint, publisher, ct)));

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
            StopTokenRenewal(key);
            throw;
        }

        if (!conn.IsOpen)
        {
            // Broker terminated the connection beyond auto-recovery's reach.
            // Evict only if our entry is still the cached one (avoid racing another caller).
            ((ICollection<KeyValuePair<string, Lazy<Task<IConnection>>>>)_connections)
                .Remove(new KeyValuePair<string, Lazy<Task<IConnection>>>(key, lazy));
            StopTokenRenewal(key);

            try { await conn.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogDebug(ex, "RabbitMQ: error disposing dead connection [{Key}]", key); }

            return await GetOrCreateConnectionAsync(endpoint, use, ct).ConfigureAwait(false);
        }

        return conn;
    }

    private const string PublisherKeySuffix = "|publisher";

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

        // A pooled connection is one TLS identity: another server name or client certificate is another connection.
        return string.Concat(
            "inline:",
            opts.Host, ":", opts.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "/", opts.VirtualHost,
            "@", opts.Username,
            opts.Ssl ? "#ssl" : string.Empty,
            opts.Ssl && !string.IsNullOrEmpty(opts.SslServerName) ? ":sni=" + opts.SslServerName : string.Empty,
            opts.Ssl && !string.IsNullOrEmpty(opts.SslCertPath) ? ":cert=" + opts.SslCertPath : string.Empty,
            ConnectionSettingsSuffix(opts));
    }

    private static readonly RabbitMQEndpointOptions DefaultOptions = new();

    /// <summary>
    /// The connection settings that differ from the defaults. A shared connection has one set of them, so an endpoint
    /// with another heartbeat, timeout, recovery policy, client name or password gets a connection of its own rather than
    /// the first endpoint's. The password enters only as a short hash: the key is written to the log.
    /// </summary>
    private static string ConnectionSettingsSuffix(RabbitMQEndpointOptions o)
    {
        var d = DefaultOptions;
        var sb = new System.Text.StringBuilder();
        void Add<T>(string name, T value, T fallback)
        {
            if (!EqualityComparer<T>.Default.Equals(value, fallback))
                sb.Append('|').Append(name).Append('=').Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        Add("heartbeat", o.Heartbeat, d.Heartbeat);
        Add("connectionTimeout", o.ConnectionTimeout, d.ConnectionTimeout);
        Add("socketReadTimeout", o.SocketReadTimeout, d.SocketReadTimeout);
        Add("socketWriteTimeout", o.SocketWriteTimeout, d.SocketWriteTimeout);
        Add("continuationTimeout", o.ContinuationTimeout, d.ContinuationTimeout);
        Add("automaticRecovery", o.AutomaticRecovery, d.AutomaticRecovery);
        Add("topologyRecoveryEnabled", o.TopologyRecoveryEnabled, d.TopologyRecoveryEnabled);
        Add("recoveryInterval", o.RecoveryInterval, d.RecoveryInterval);
        Add("consumerDispatchConcurrency", o.ConsumerDispatchConcurrency, d.ConsumerDispatchConcurrency);
        Add("clientName", o.ClientName, d.ClientName);
        Add("sslCaCertPath", o.SslCaCertPath, d.SslCaCertPath);
        Add("sslProtocols", o.SslProtocols, d.SslProtocols);
        Add("revocationMode", o.RevocationMode, d.RevocationMode);
        Add("revocationSoftFail", o.RevocationSoftFail, d.RevocationSoftFail);
        Add("authMechanism", o.AuthMechanism, d.AuthMechanism);
        if (o.Password != d.Password)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(o.Password));
            sb.Append("|pw=").Append(Convert.ToHexString(hash, 0, 4));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the underlying <see cref="ConnectionFactory"/> and opens the physical connection.
    /// Honours <see cref="RabbitMQEndpointOptions.ConnectionFactory"/> registry lookup;
    /// supports comma-separated host lists for cluster mode.
    /// </summary>
    private async Task<IConnection> CreateConnectionAsync(string key, RabbitMQEndpoint endpoint, bool publisher, CancellationToken ct)
    {
        var (factory, hosts, oauth2) = ResolveConnectionTarget(endpoint.EndpointOptions);
        if (publisher)
            factory.ClientProvidedName = $"{factory.ClientProvidedName} (publisher)";

        OAuth2ClientCredentialsProvider? tokens = null;
        if (oauth2 is not null)
        {
            var builder = new OAuth2ClientBuilder(oauth2.OAuth2ClientId!, oauth2.OAuth2ClientSecret!, new Uri(oauth2.OAuth2TokenEndpoint!), null);
            if (!string.IsNullOrEmpty(oauth2.OAuth2Scope))
                builder.SetScope(oauth2.OAuth2Scope);
            tokens = new OAuth2ClientCredentialsProvider(key, await builder.BuildAsync(ct).ConfigureAwait(false));
            factory.CredentialsProvider = tokens;
        }

        // Always over the endpoint list, even for one host: that overload takes TLS from each endpoint,
        // and the same list serves failover on the first connect and on recovery.
        IConnection connection;
        try
        {
            connection = await factory.CreateConnectionAsync(hosts, ct).ConfigureAwait(false);
        }
        catch
        {
            tokens?.Dispose();
            throw;
        }

        endpoint.AttachConnectionLifecycleHandlers(connection);

        if (tokens is not null)
            _tokenRenewals[key] = new TokenRenewal(tokens, new CredentialsRefresher(tokens,
                (credentials, exception, token) => RenewSecretAsync(key, connection, credentials, exception, token),
                CancellationToken.None));

        Logger?.LogInformation(
            "RabbitMQ connection opened [{Key}]: host={Host}:{Port}, tls={Tls}, vHost={VHost}, login={Login}",
            key, connection.Endpoint.HostName, connection.Endpoint.Port, connection.Endpoint.Ssl.Enabled,
            factory.VirtualHost, LoginOf(factory, oauth2));

        return connection;
    }

    private static string LoginOf(ConnectionFactory factory, RabbitMQConnectionFactory? oauth2) =>
        oauth2 is not null ? $"oauth2 ({oauth2.OAuth2ClientId})"
        : factory.AuthMechanisms.Any(m => m is ExternalMechanismFactory) ? "client certificate (EXTERNAL)"
        : factory.CredentialsProvider is { } provider && provider is not BasicCredentialsProvider ? $"credentials provider ({provider.Name})"
        : factory.UserName;

    /// <summary>
    /// Puts a renewed access token on the open connection (<c>connection.update-secret</c>), before the old one expires.
    /// A failed renewal is logged: the broker closes the connection once the old token expires, and recovery then logs in
    /// with a fresh token.
    /// </summary>
    private async Task RenewSecretAsync(string key, IConnection connection, Credentials? credentials, Exception? exception, CancellationToken ct)
    {
        if (exception is not null || credentials is null)
        {
            Logger?.LogError(exception, "RabbitMQ [{Key}]: the OAuth2 access token could not be renewed", key);
            return;
        }

        if (!connection.IsOpen)
            return;

        await connection.UpdateSecretAsync(credentials.Password, "OAuth2 access token renewed", ct).ConfigureAwait(false);
        Logger?.LogDebug("RabbitMQ [{Key}]: OAuth2 access token renewed on the open connection", key);
    }

    /// <summary>The token source of an OAuth2 connection and the timer that renews its token.</summary>
    private sealed record TokenRenewal(OAuth2ClientCredentialsProvider Tokens, CredentialsRefresher Refresher) : IDisposable
    {
        public void Dispose()
        {
            Refresher.Dispose();
            Tokens.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, TokenRenewal> _tokenRenewals = new(StringComparer.Ordinal);

    private void StopTokenRenewal(string key)
    {
        if (_tokenRenewals.TryRemove(key, out var renewal))
            renewal.Dispose();
    }

    /// <summary>
    /// The factory and the hosts it connects to, from one source: the named factory's own settings and host list,
    /// or the endpoint URI's. Every host carries the TLS settings. The named factory comes back too when it logs in
    /// with OAuth2, which is set up asynchronously when the connection opens.
    /// </summary>
    private (ConnectionFactory Factory, IReadOnlyList<AmqpTcpEndpoint> Hosts, RabbitMQConnectionFactory? OAuth2)
        ResolveConnectionTarget(RabbitMQEndpointOptions options)
    {
        // Named factory from registry — its settings are authoritative; inline overrides are not
        // applied (matches the historical contract of RabbitMQConnectionFactory.Build()).
        if (!string.IsNullOrEmpty(options.ConnectionFactory))
        {
            // A set-but-unknown name fails loud — never a silent fallback to URI params (Ф11 Ж-1).
            var registryFactory = Context.GetRequiredFromRegistry<RabbitMQConnectionFactory>(options.ConnectionFactory);
            registryFactory.Validate(options.ConnectionFactory);
            Logger?.LogDebug("RabbitMQ: using ConnectionFactory '{Name}' from registry", options.ConnectionFactory);
            var factoryHosts = registryFactory.GetEndpoints($"RabbitMQ connection factory '{options.ConnectionFactory}'", Logger);
            return (registryFactory.Build(factoryHosts), factoryHosts, registryFactory.UsesOAuth2 ? registryFactory : null);
        }

        var hosts = RabbitMQConnectionFactory.BuildEndpoints(
            options.Host, options.Port, options.ResolveTls("RabbitMQ endpoint"), Logger);

        var factory = new ConnectionFactory
        {
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
        };
        if (options.AuthMechanism == RabbitMQAuthMechanism.External)
            factory.AuthMechanisms = [new ExternalMechanismFactory()];

        // The Endpoint setter also sets factory.Ssl from the endpoint, so TLS is never set apart from the hosts.
        factory.Endpoint = hosts[0];
        return (factory, hosts, null);
    }

    /// <summary>
    /// Closes every pooled connection. Called by <see cref="RouteContext.DisposeAsync"/>;
    /// individual endpoint <c>Stop</c> calls do not close pooled connections (channels only),
    /// so connections survive Stop/Start cycles intentionally.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        foreach (var key in _tokenRenewals.Keys)
            StopTokenRenewal(key);

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

