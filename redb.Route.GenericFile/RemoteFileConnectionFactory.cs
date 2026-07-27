using redb.Route.Abstractions;

namespace redb.Route.GenericFile;

/// <summary>
/// Base named connection factory for remote-file transports (FTP, SFTP). Register a concrete
/// subclass in the route registry and reference it by name from the endpoint URI
/// (<c>connectionFactory=my-server</c>) so server credentials never have to appear in the URI,
/// and therefore can never reach logs, telemetry, or the Tsak dashboard.
/// </summary>
public abstract class RemoteFileConnectionFactory
{
    /// <summary>Remote server hostname or IP.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Server port (0 = protocol default).</summary>
    public int Port { get; set; }

    /// <summary>Username for authentication.</summary>
    public string Username { get; set; } = "";

    /// <summary>Password for authentication.</summary>
    public string Password { get; set; } = "";

    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectionTimeout { get; set; } = 30_000;

    /// <summary>Operation timeout in milliseconds.</summary>
    public int OperationTimeout { get; set; } = 60_000;

    /// <summary>
    /// Copies the shared connection and credential settings onto the endpoint options, but only for
    /// parameters the endpoint URI did not set explicitly — an inline URI value always wins, so
    /// existing routes keep their behaviour. Subclasses override to add protocol-specific fields
    /// and must call <c>base.ApplyTo(...)</c> first.
    /// </summary>
    internal virtual void ApplyTo(RemoteFileEndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(options.Host))) options.Host = Host;
        if (!supplied.ContainsKey(nameof(options.Port))) options.Port = Port;
        if (!supplied.ContainsKey(nameof(options.Username))) options.Username = Username;
        if (!supplied.ContainsKey(nameof(options.Password))) options.Password = Password;
        if (!supplied.ContainsKey(nameof(options.ConnectionTimeout)))
            options.ConnectionTimeout = ConnectionTimeout;
        if (!supplied.ContainsKey(nameof(options.OperationTimeout)))
            options.OperationTimeout = OperationTimeout;
    }
}
