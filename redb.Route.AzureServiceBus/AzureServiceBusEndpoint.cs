using Azure.Messaging.ServiceBus;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// Azure Service Bus endpoint. Manages shared <see cref="ServiceBusClient"/> lifecycle.
/// Queue/topic name comes from <see cref="EndpointUri.Path"/>.
/// </summary>
internal sealed class AzureServiceBusEndpoint : EndpointBase<AzureServiceBusEndpointOptions>
{
    private ServiceBusClient? _client;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    internal AzureServiceBusEndpoint(EndpointUri uri, AzureServiceBusComponent component,
        AzureServiceBusEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>Queue or topic name resolved from options or URI path.</summary>
    internal string EntityName => Options.QueueName ?? Options.TopicName ?? Uri.Path;

    /// <summary>True when a subscription name is set (topic mode).</summary>
    internal bool IsTopic => !string.IsNullOrEmpty(Options.SubscriptionName);

    /// <summary>
    /// Returns a shared <see cref="ServiceBusClient"/> for this endpoint.
    /// Thread-safe; ServiceBusClient is designed for long-lived reuse.
    /// </summary>
    internal async Task<ServiceBusClient> GetOrCreateClientAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;

            if (!string.IsNullOrWhiteSpace(Options.ConnectionFactory)
                && Component is ComponentBase cb && cb.Context is not null)
            {
                var factory = cb.Context.GetFromRegistry<AzureServiceBusConnectionFactory>(
                    Options.ConnectionFactory);
                if (factory is not null)
                {
                    _client = factory.Build();
                    return _client;
                }
            }

            _client = BuildClientFromOptions();
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
        => new AzureServiceBusProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        if (Options.EnableSessions)
            return new AzureServiceBusSessionConsumer(this, processor, Options);
        return new AzureServiceBusConsumer(this, processor, Options);
    }

    /// <inheritdoc />
    public override async Task Stop(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        await base.Stop(ct).ConfigureAwait(false);
    }

    private ServiceBusClient BuildClientFromOptions()
    {
        var clientOptions = new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions
            {
                Mode = Options.ParsedRetryMode,
                MaxRetries = Options.RetryMaxRetries,
                Delay = TimeSpan.FromMilliseconds(Options.RetryDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(Options.RetryMaxDelayMs)
            }
        };
        return new ServiceBusClient(Options.ConnectionString, clientOptions);
    }
}
