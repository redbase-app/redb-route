using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using global::RabbitMQ.Client;
using Microsoft.Extensions.Logging;
using redb.Route.Core;

namespace redb.Route.RabbitMQ;

/// <summary>
/// Connection factory configuration for RabbitMQ. Register in the route registry
/// and reference by name in endpoint URIs (<c>connectionFactory=myFactory</c>).
/// Supports cluster mode via comma-separated hosts.
/// </summary>
public sealed class RabbitMQConnectionFactory
{
    /// <summary>Host name or comma-separated list for cluster mode.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>AMQP port (default: 5672; the broker's TLS listener is usually 5671).</summary>
    public int Port { get; set; } = 5672;

    /// <summary>Username (default: guest). Not used with <see cref="RabbitMQAuthMechanism.External"/> or OAuth2.</summary>
    public string Username { get; set; } = "guest";

    /// <summary>Password (default: guest). Not used with <see cref="RabbitMQAuthMechanism.External"/> or OAuth2.</summary>
    [Sensitive]
    public string Password { get; set; } = "guest";

    /// <summary>Virtual host (default: /).</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Enable automatic recovery (default: true).</summary>
    public bool AutomaticRecovery { get; set; } = true;

    /// <summary>Re-declare topology (exchanges, queues, bindings) after recovery (default: true). Critical for cluster.</summary>
    public bool TopologyRecoveryEnabled { get; set; } = true;

    /// <summary>Network recovery interval in seconds (default: 5).</summary>
    public int RecoveryInterval { get; set; } = 5;

    /// <summary>Requested heartbeat in seconds (default: 60).</summary>
    public int Heartbeat { get; set; } = 60;

    /// <summary>Connection timeout in seconds (default: 60). Prevents hangs on unreachable cluster nodes.</summary>
    public int ConnectionTimeout { get; set; } = 60;

    /// <summary>Socket read timeout in seconds (default: 30). Detects dead TCP connections faster than heartbeat alone.</summary>
    public int SocketReadTimeout { get; set; } = 30;

    /// <summary>Socket write timeout in seconds (default: 30).</summary>
    public int SocketWriteTimeout { get; set; } = 30;

    /// <summary>Timeout for AMQP protocol-level continuations in seconds (default: 30). Prevents indefinite hangs on declare/bind.</summary>
    public int ContinuationTimeout { get; set; } = 30;

    /// <summary>Concurrent dispatch limit per connection (default: 1). Controls how many messages the client dispatches concurrently.</summary>
    public ushort ConsumerDispatchConcurrency { get; set; } = 1;

    // ── TLS ──

    /// <summary>Enable TLS for the connection.</summary>
    public bool Ssl { get; set; }

    /// <summary>
    /// The name the broker certificate must carry. Unset, each host of the list is checked against its own name; set it
    /// when the certificate names a load balancer or an alias.
    /// </summary>
    public string? SslServerName { get; set; }

    /// <summary>Client certificate (PFX) presented for mutual TLS. Or give the certificate itself as <see cref="ClientCertificate"/>.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Passphrase of <see cref="SslCertPath"/>.</summary>
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Client certificate for mutual TLS, with its private key — from the Windows certificate store, a key vault or
    /// anywhere else. Checked for its validity period when the connection opens.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; set; }

    /// <summary>PEM file with the root certificates the broker certificate must chain to, instead of the system trust store.</summary>
    public string? SslCaCertPath { get; set; }

    /// <summary>The root certificates the broker certificate must chain to, instead of the system trust store.</summary>
    public X509Certificate2Collection? SslCaCertificates { get; set; }

    /// <summary>Allowed TLS versions (Tls12, Tls13). <see cref="SslProtocols.None"/> (default): the operating system chooses.</summary>
    public SslProtocols SslProtocols { get; set; }

    /// <summary>
    /// Revocation check of the broker certificate: <see cref="X509RevocationMode.NoCheck"/> (default),
    /// <see cref="X509RevocationMode.Online"/> or <see cref="X509RevocationMode.Offline"/>.
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; }

    /// <summary>Accept a broker certificate whose revocation status cannot be determined. Default false: refused.</summary>
    public bool RevocationSoftFail { get; set; }

    // ── Authentication ──

    /// <summary>
    /// <see cref="RabbitMQAuthMechanism.Plain"/> (default) or <see cref="RabbitMQAuthMechanism.External"/>: the TLS client
    /// certificate is the login.
    /// </summary>
    public RabbitMQAuthMechanism AuthMechanism { get; set; }

    /// <summary>
    /// OAuth 2.0 token endpoint (client credentials grant). Set with <see cref="OAuth2ClientId"/> and
    /// <see cref="OAuth2ClientSecret"/>, the connection logs in with an access token instead of a password and renews
    /// it on the open connection before it expires. HTTPS, except on the loopback address. Needs the broker's
    /// <c>rabbitmq_auth_backend_oauth2</c> plugin.
    /// </summary>
    public string? OAuth2TokenEndpoint { get; set; }

    /// <summary>OAuth 2.0 client id.</summary>
    public string? OAuth2ClientId { get; set; }

    /// <summary>OAuth 2.0 client secret.</summary>
    [Sensitive]
    public string? OAuth2ClientSecret { get; set; }

    /// <summary>Requested scopes, space-separated (optional; for RabbitMQ usually <c>rabbitmq.read:*/* ...</c>).</summary>
    public string? OAuth2Scope { get; set; }

    /// <summary>
    /// Your own source of credentials (<see cref="ICredentialsProvider"/>), in place of <see cref="Username"/> and
    /// <see cref="Password"/> — a secret store, a token of another identity provider. Not combined with OAuth2.
    /// </summary>
    public ICredentialsProvider? CredentialsProvider { get; set; }

    /// <summary>Client-provided connection name for management UI.</summary>
    public string ClientName { get; set; } = "redb.Route";

    internal bool UsesOAuth2 => !string.IsNullOrEmpty(OAuth2TokenEndpoint);

    /// <summary>Throws when the factory's settings contradict each other; called when a connection is opened with it.</summary>
    /// <param name="name">The factory's registry name, for the error text.</param>
    public void Validate(string name)
    {
        var owner = $"RabbitMQ connection factory '{name}'";
        RabbitMQTls.Validate(new RabbitMQTls.TlsRules(
            Ssl, SslServerName, SslCertPath, SslCertPassword, ClientCertificate is not null,
            SslCaCertPath, SslCaCertificates is not null, SslProtocols, RevocationMode, RevocationSoftFail, AuthMechanism), owner);

        var oauthSet = new[] { OAuth2TokenEndpoint, OAuth2ClientId, OAuth2ClientSecret }.Count(v => !string.IsNullOrEmpty(v));
        if (oauthSet is > 0 and < 3)
            throw new ArgumentException($"{owner}: OAuth2 needs OAuth2TokenEndpoint, OAuth2ClientId and OAuth2ClientSecret together.");
        if (!string.IsNullOrEmpty(OAuth2Scope) && !UsesOAuth2)
            throw new ArgumentException($"{owner}: OAuth2Scope is given without OAuth2TokenEndpoint.");

        var logins = new List<string>();
        if (UsesOAuth2) logins.Add("OAuth2");
        if (CredentialsProvider is not null) logins.Add("CredentialsProvider");
        if (AuthMechanism == RabbitMQAuthMechanism.External) logins.Add("AuthMechanism=External");
        if (logins.Count > 1)
            throw new ArgumentException($"{owner}: {string.Join(" and ", logins)} are two ways to log in; choose one.");

        if (UsesOAuth2)
        {
            if (!Uri.TryCreate(OAuth2TokenEndpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
                throw new ArgumentException(
                    $"{owner}: OAuth2TokenEndpoint '{OAuth2TokenEndpoint}' must be an absolute https URI (http only on the loopback " +
                    "address): the client secret is sent to it.");
        }
    }

    /// <summary>
    /// Creates a configured <see cref="ConnectionFactory"/> pointed at the first host. Open the connection with
    /// <c>CreateConnectionAsync(</c><see cref="GetEndpoints()"/><c>)</c> to fail over across every host of the cluster.
    /// OAuth2 credentials are set up by the component when it opens the connection (the token is fetched asynchronously
    /// and renewed on the open connection).
    /// </summary>
    public ConnectionFactory Build() => Build(GetEndpoints());

    internal ConnectionFactory Build(IReadOnlyList<AmqpTcpEndpoint> endpoints)
    {
        var factory = new ConnectionFactory
        {
            UserName = Username,
            Password = Password,
            VirtualHost = VirtualHost,
            AutomaticRecoveryEnabled = AutomaticRecovery,
            TopologyRecoveryEnabled = TopologyRecoveryEnabled,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(RecoveryInterval),
            RequestedHeartbeat = TimeSpan.FromSeconds(Heartbeat),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(ConnectionTimeout),
            SocketReadTimeout = TimeSpan.FromSeconds(SocketReadTimeout),
            SocketWriteTimeout = TimeSpan.FromSeconds(SocketWriteTimeout),
            ContinuationTimeout = TimeSpan.FromSeconds(ContinuationTimeout),
            ConsumerDispatchConcurrency = ConsumerDispatchConcurrency,
            ClientProvidedName = ClientName,
        };
        if (AuthMechanism == RabbitMQAuthMechanism.External)
            factory.AuthMechanisms = [new ExternalMechanismFactory()];
        if (CredentialsProvider is not null)
            factory.CredentialsProvider = CredentialsProvider;

        // The Endpoint setter also replaces factory.Ssl with the endpoint's own TLS settings,
        // so TLS travels with the endpoint and is never set on the factory separately.
        factory.Endpoint = endpoints[0];
        return factory;
    }

    /// <summary>
    /// Parses the host list into AMQP endpoints, each carrying the TLS settings: a connection opened over an
    /// endpoint list takes TLS from the endpoints, not from the <see cref="ConnectionFactory"/>.
    /// </summary>
    public List<AmqpTcpEndpoint> GetEndpoints() => GetEndpoints("RabbitMQ connection factory", null);

    internal List<AmqpTcpEndpoint> GetEndpoints(string owner, ILogger? logger)
        => BuildEndpoints(Host, Port, ResolveTls(owner), logger);

    /// <summary>The TLS settings of this factory, loading the certificate files it names.</summary>
    internal RabbitMQTlsSettings ResolveTls(string owner)
    {
        if (!Ssl)
            return RabbitMQTlsSettings.Off;

        var client = ClientCertificate;
        if (client is not null)
            RabbitMQTls.EnsureClientCertificateUsable(client, owner);
        else if (!string.IsNullOrEmpty(SslCertPath))
            client = RabbitMQTls.LoadClientCertificate(SslCertPath, SslCertPassword, owner);

        var roots = SslCaCertificates
            ?? (string.IsNullOrEmpty(SslCaCertPath) ? null : RabbitMQTls.LoadCaCertificates(SslCaCertPath, owner));

        return new RabbitMQTlsSettings(true, SslServerName, client, SslProtocols, roots, RevocationMode, RevocationSoftFail);
    }

    /// <summary>
    /// The AMQP endpoints of a comma-separated host list, each with the TLS settings. Without an explicit server name
    /// each endpoint checks the broker certificate against its own host.
    /// </summary>
    internal static List<AmqpTcpEndpoint> BuildEndpoints(string hostList, int port, RabbitMQTlsSettings tls, ILogger? logger)
    {
        var hosts = hostList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0)
            throw new InvalidOperationException("RabbitMQ: no host is configured; set host (a name or a comma-separated list).");

        return hosts.Select(h => new AmqpTcpEndpoint(h, port, RabbitMQTls.OptionFor(h, tls, logger))).ToList();
    }
}
