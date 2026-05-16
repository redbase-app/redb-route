using System.Security.Authentication;

namespace redb.Route.Amqp;

/// <summary>
/// AMQP 1.0 connection factory configuration. Standalone POCO — register in DI or reference by name.
/// <para>
/// Covers all <see cref="global::Amqp.ConnectionFactory"/> settings:
/// SASL, SSL/TLS, TCP tuning, AMQP protocol limits, and reconnect policy.
/// </para>
/// </summary>
public sealed class AmqpConnectionFactory
{
    // ── Connection coordinates ──

    /// <summary>Broker host (default: localhost). Comma-separated for failover.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>AMQP port (default: 5672, or 5671 for AMQPS).</summary>
    public int Port { get; set; } = 5672;

    /// <summary>Username for SASL PLAIN authentication.</summary>
    public string? User { get; set; }

    /// <summary>Password for SASL PLAIN authentication.</summary>
    public string? Password { get; set; }

    /// <summary>Container ID sent during AMQP Open. Identifies this client to the broker.</summary>
    public string? ContainerId { get; set; }

    /// <summary>Virtual host / AMQP hostname field (used by Azure Service Bus, multi-tenant brokers).</summary>
    public string? VirtualHost { get; set; }

    /// <summary>Client name for logging/diagnostics (maps to ContainerId if not set).</summary>
    public string? ClientName { get; set; }

    // ── AMQP protocol limits ──

    /// <summary>Idle timeout in milliseconds. 0 = disabled. (default: 120000)</summary>
    public int IdleTimeout { get; set; } = 120_000;

    /// <summary>Maximum frame size in bytes. (default: 256KB)</summary>
    public int MaxFrameSize { get; set; } = 256 * 1024;

    /// <summary>Maximum sessions per connection. (default: 8)</summary>
    public ushort MaxSessions { get; set; } = 8;

    /// <summary>Maximum links (senders+receivers) per session. 0 = unlimited. (default: 0)</summary>
    public int MaxLinksPerSession { get; set; }

    // ── TCP tuning ──

    /// <summary>TCP NoDelay (Nagle off). (default: true)</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>TCP keepalive. (default: false)</summary>
    public bool KeepAlive { get; set; }

    /// <summary>TCP send buffer size in bytes. 0 = OS default.</summary>
    public int SendBufferSize { get; set; }

    /// <summary>TCP receive buffer size in bytes. 0 = OS default.</summary>
    public int ReceiveBufferSize { get; set; }

    /// <summary>TCP send timeout in milliseconds. 0 = infinite.</summary>
    public int SendTimeout { get; set; }

    /// <summary>TCP receive timeout in milliseconds. 0 = infinite.</summary>
    public int ReceiveTimeout { get; set; }

    // ── SSL/TLS ──

    /// <summary>Enable SSL/TLS (amqps://). (default: false)</summary>
    public bool Ssl { get; set; }

    /// <summary>SSL protocols to use. (default: None — OS negotiates)</summary>
    public SslProtocols SslProtocols { get; set; } = SslProtocols.None;

    /// <summary>Path to client certificate file (PFX/PEM) for mTLS.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for client certificate.</summary>
    public string? SslCertPassword { get; set; }

    /// <summary>Check certificate revocation. (default: false)</summary>
    public bool CheckCertRevocation { get; set; }

    /// <summary>Skip server certificate validation (DANGEROUS — dev only). (default: false)</summary>
    public bool SkipServerCertValidation { get; set; }

    // ── SASL ──

    /// <summary>
    /// SASL mechanism: PLAIN, EXTERNAL, ANONYMOUS. (default: PLAIN if User is set, ANONYMOUS otherwise)
    /// </summary>
    public SaslMechanism SaslMechanism { get; set; } = SaslMechanism.Auto;

    // ── Reconnect ──

    /// <summary>Enable automatic reconnect. (default: false — caller is responsible)</summary>
    public bool Reconnect { get; set; }

    /// <summary>Reconnect interval in milliseconds. (default: 5000)</summary>
    public int ReconnectInterval { get; set; } = 5000;

    /// <summary>Max reconnect attempts. 0 = infinite. (default: 0)</summary>
    public int MaxReconnectAttempts { get; set; }

    // ── Build ──

    /// <summary>
    /// Builds a native <see cref="global::Amqp.ConnectionFactory"/> configured from these settings.
    /// </summary>
    public global::Amqp.ConnectionFactory Build()
    {
        var factory = new global::Amqp.ConnectionFactory();

        // AMQP settings
        var containerId = ContainerId ?? ClientName ?? $"redb-route-{Guid.NewGuid():N}";
        factory.AMQP.ContainerId = containerId;
        factory.AMQP.HostName = VirtualHost;
        factory.AMQP.IdleTimeout = IdleTimeout;
        factory.AMQP.MaxFrameSize = MaxFrameSize;
        factory.AMQP.MaxSessionsPerConnection = MaxSessions;

        // TCP settings
        factory.TCP.NoDelay = NoDelay;
        if (KeepAlive)
            factory.TCP.KeepAlive = new global::Amqp.TcpKeepAliveSettings { KeepAliveTime = 30000, KeepAliveInterval = 10000 };
        if (SendBufferSize > 0) factory.TCP.SendBufferSize = SendBufferSize;
        if (ReceiveBufferSize > 0) factory.TCP.ReceiveBufferSize = ReceiveBufferSize;
        if (SendTimeout > 0) factory.TCP.SendTimeout = SendTimeout;
        if (ReceiveTimeout > 0) factory.TCP.ReceiveTimeout = ReceiveTimeout;

        // SASL
        ConfigureSasl(factory);

        // SSL
        if (Ssl)
            ConfigureSsl(factory);

        return factory;
    }

    /// <summary>
    /// Builds an <see cref="global::Amqp.Address"/> from the current host/port/user/password settings.
    /// </summary>
    public global::Amqp.Address BuildAddress()
    {
        var scheme = Ssl ? "amqps" : "amqp";
        return new global::Amqp.Address(Host, Port, User, Password, "/", scheme);
    }

    /// <summary>
    /// Parses comma-separated hosts and returns an array of addresses for failover.
    /// </summary>
    public global::Amqp.Address[] GetAddresses()
    {
        var hosts = Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length <= 1)
            return [BuildAddress()];

        var scheme = Ssl ? "amqps" : "amqp";
        return hosts.Select(h => new global::Amqp.Address(h, Port, User, Password, "/", scheme)).ToArray();
    }

    private void ConfigureSasl(global::Amqp.ConnectionFactory factory)
    {
        var mechanism = SaslMechanism;
        if (mechanism == SaslMechanism.Auto)
        {
            mechanism = !string.IsNullOrEmpty(User)
                ? SaslMechanism.Plain
                : SaslMechanism.Anonymous;
        }

        // SASL PLAIN is handled automatically by AMQPNetLite when User/Password are in the Address.
        // Only External and Anonymous need explicit profile configuration.
        factory.SASL.Profile = mechanism switch
        {
            SaslMechanism.External => global::Amqp.Sasl.SaslProfile.External,
            SaslMechanism.Anonymous => global::Amqp.Sasl.SaslProfile.Anonymous,
            _ => null  // PLAIN: auto-detected from Address credentials
        };
    }

    private void ConfigureSsl(global::Amqp.ConnectionFactory factory)
    {
        factory.SSL.Protocols = SslProtocols;
        factory.SSL.CheckCertificateRevocation = CheckCertRevocation;

        if (!string.IsNullOrEmpty(SslCertPath))
        {
            var cert = string.IsNullOrEmpty(SslCertPassword)
                ? new System.Security.Cryptography.X509Certificates.X509Certificate2(SslCertPath)
                : new System.Security.Cryptography.X509Certificates.X509Certificate2(SslCertPath, SslCertPassword);
            factory.SSL.ClientCertificates.Add(cert);
        }

        if (SkipServerCertValidation)
        {
            factory.SSL.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
    }
}

/// <summary>SASL authentication mechanism.</summary>
public enum SaslMechanism
{
    /// <summary>Auto-detect: PLAIN if User is set, ANONYMOUS otherwise.</summary>
    Auto,

    /// <summary>SASL PLAIN (username/password).</summary>
    Plain,

    /// <summary>SASL EXTERNAL (client certificate).</summary>
    External,

    /// <summary>SASL ANONYMOUS (no credentials).</summary>
    Anonymous
}
