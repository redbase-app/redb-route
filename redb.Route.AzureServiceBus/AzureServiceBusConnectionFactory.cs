using Azure.Messaging.ServiceBus;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// POCO factory for creating <see cref="ServiceBusClient"/> instances.
/// Register in the route context registry for shared client configuration:
/// <code>context.AddToRegistry("myAsb", new AzureServiceBusConnectionFactory { ... });</code>
/// Reference from URI: <c>asb://queue?connectionFactory=myAsb</c>
/// </summary>
public sealed class AzureServiceBusConnectionFactory
{
    /// <summary>Azure Service Bus connection string.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Maximum number of retries (default 3).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Initial retry delay in milliseconds (default 800).</summary>
    public int DelayMs { get; set; } = 800;

    /// <summary>Maximum retry delay in milliseconds (default 60000).</summary>
    public int MaxDelayMs { get; set; } = 60_000;

    /// <summary>Retry mode: Exponential or Fixed (default Exponential).</summary>
    public ServiceBusRetryMode RetryMode { get; set; } = ServiceBusRetryMode.Exponential;

    /// <summary>Per-try timeout in milliseconds (default 60000).</summary>
    public int TryTimeoutMs { get; set; } = 60_000;

    /// <summary>Transport type: AmqpTcp or AmqpWebSockets (default AmqpTcp).</summary>
    public ServiceBusTransportType TransportType { get; set; } = ServiceBusTransportType.AmqpTcp;

    /// <summary>Web proxy address. Forces AmqpWebSockets transport when set.</summary>
    public string? ProxyAddress { get; set; }

    /// <summary>Builds a configured <see cref="ServiceBusClient"/>.</summary>
    public ServiceBusClient Build()
    {
        var options = new ServiceBusClientOptions
        {
            TransportType = TransportType,
            RetryOptions = new ServiceBusRetryOptions
            {
                Mode = RetryMode,
                MaxRetries = MaxRetries,
                Delay = TimeSpan.FromMilliseconds(DelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(MaxDelayMs),
                TryTimeout = TimeSpan.FromMilliseconds(TryTimeoutMs)
            }
        };

        if (!string.IsNullOrWhiteSpace(ProxyAddress))
        {
            options.TransportType = ServiceBusTransportType.AmqpWebSockets;
            options.WebProxy = new System.Net.WebProxy(ProxyAddress);
        }

        return new ServiceBusClient(ConnectionString, options);
    }
}
