using global::RabbitMQ.Client;

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

    /// <summary>AMQP port (default: 5672).</summary>
    public int Port { get; set; } = 5672;

    /// <summary>Username (default: guest).</summary>
    public string Username { get; set; } = "guest";

    /// <summary>Password (default: guest).</summary>
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

    // ── SSL ──

    /// <summary>Enable SSL/TLS for the connection.</summary>
    public bool Ssl { get; set; }

    /// <summary>SSL server name for certificate validation.</summary>
    public string? SslServerName { get; set; }

    /// <summary>Path to client certificate file (for mTLS).</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Client certificate passphrase.</summary>
    public string? SslCertPassphrase { get; set; }

    /// <summary>Client-provided connection name for management UI.</summary>
    public string ClientName { get; set; } = "redb.Route";

    /// <summary>Creates a configured <see cref="ConnectionFactory"/>.</summary>
    public ConnectionFactory Build()
    {
        var endpoints = GetEndpoints();
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

        if (Ssl)
        {
            factory.Ssl = new global::RabbitMQ.Client.SslOption
            {
                Enabled = true,
                ServerName = SslServerName,
                CertPath = SslCertPath,
                CertPassphrase = SslCertPassphrase,
            };
        }

        // Set endpoint to first host for single-node compatibility
        factory.Endpoint = endpoints[0];
        return factory;
    }

    /// <summary>Parses host list into AMQP endpoints.</summary>
    public List<AmqpTcpEndpoint> GetEndpoints()
    {
        return Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => new AmqpTcpEndpoint(h, Port))
            .ToList();
    }
}
