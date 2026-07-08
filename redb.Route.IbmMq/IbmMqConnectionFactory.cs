using System.Collections;
using IBM.WMQ;

namespace redb.Route.IbmMq;

/// <summary>
/// IBM MQ connection factory configuration. Standalone POCO — register in DI or reference by name.
/// <para>
/// Encapsulates all <see cref="MQQueueManager"/> connection properties:
/// host/port/channel, authentication, SSL/TLS, and tuning.
/// </para>
/// </summary>
public sealed class IbmMqConnectionFactory
{
    // ── Connection coordinates ──

    /// <summary>Queue manager hostname. Comma-separated for multi-instance failover. (default: localhost)</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>MQ listener port. (default: 1414)</summary>
    public int Port { get; set; } = 1414;

    /// <summary>Server-connection channel. (default: DEV.APP.SVRCONN)</summary>
    public string Channel { get; set; } = "DEV.APP.SVRCONN";

    /// <summary>Queue manager name.</summary>
    public string QueueManager { get; set; } = "QM1";

    /// <summary>Authentication user.</summary>
    public string? User { get; set; }

    /// <summary>Authentication password.</summary>
    public string? Password { get; set; }

    /// <summary>Client name for connection tracking.</summary>
    public string? ClientName { get; set; }

    // ── Transport ──

    /// <summary>Transport type: client (TCP), bindings (local), or managed. (default: managed client)</summary>
    public string TransportType { get; set; } = MQC.TRANSPORT_MQSERIES_MANAGED;

    // ── SSL/TLS ──

    /// <summary>SSL CipherSpec (e.g. TLS_RSA_WITH_AES_256_CBC_SHA256).</summary>
    public string? SslCipherSpec { get; set; }

    /// <summary>SSL certificate label in keystore.</summary>
    public string? SslCertLabel { get; set; }

    /// <summary>Distinguished Name pattern for peer validation.</summary>
    public string? SslPeerName { get; set; }

    /// <summary>Path to key repository (.kdb file, without extension).</summary>
    public string? SslKeyRepository { get; set; }

    /// <summary>Bytes before SSL secret key renegotiation. 0 = disabled.</summary>
    public int SslKeyResetCount { get; set; }

    // ── Tuning ──

    /// <summary>CCSID for the connection. (default: 1208 = UTF-8)</summary>
    public int CCSID { get; set; } = 1208;

    /// <summary>
    /// Builds a <see cref="Hashtable"/> of MQ connection properties suitable for
    /// <see cref="MQQueueManager(string, Hashtable)"/> constructor.
    /// </summary>
    public Hashtable BuildConnectionProperties()
    {
        var props = new Hashtable
        {
            { MQC.TRANSPORT_PROPERTY, TransportType },
            { MQC.HOST_NAME_PROPERTY, Host },
            { MQC.PORT_PROPERTY, Port },
            { MQC.CHANNEL_PROPERTY, Channel },
            { MQC.CCSID_PROPERTY, CCSID },
        };

        if (!string.IsNullOrEmpty(User))
            props[MQC.USER_ID_PROPERTY] = User;

        if (!string.IsNullOrEmpty(Password))
            props[MQC.PASSWORD_PROPERTY] = Password;

        if (!string.IsNullOrEmpty(ClientName))
            props[MQC.APPNAME_PROPERTY] = ClientName;

        // SSL/TLS
        if (!string.IsNullOrEmpty(SslCipherSpec))
            props[MQC.SSL_CIPHER_SPEC_PROPERTY] = SslCipherSpec;

        if (!string.IsNullOrEmpty(SslCertLabel))
            props[MQC.SSL_CERT_STORE_PROPERTY] = SslCertLabel;

        if (!string.IsNullOrEmpty(SslPeerName))
            props[MQC.SSL_PEER_NAME_PROPERTY] = SslPeerName;

        if (!string.IsNullOrEmpty(SslKeyRepository))
            MQEnvironment.SSLKeyRepository = SslKeyRepository;

        return props;
    }
}
