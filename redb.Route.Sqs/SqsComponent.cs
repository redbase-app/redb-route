using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Sqs;

/// <summary>
/// Amazon SQS transport component for redb.Route. Scheme: <c>sqs</c>.
/// <para>Consumer: <c>sqs://my-queue?waitTimeSeconds=20&amp;concurrentConsumers=4</c></para>
/// <para>Producer: <c>sqs://my-queue?region=us-east-1</c> (send to the queue).</para>
/// <para>FIFO queues are addressed by their <c>.fifo</c> name; LocalStack/ElasticMQ via <c>serviceUrl=</c>.</para>
/// </summary>
public sealed class SqsComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "sqs";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new SqsEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new SqsEndpoint(uri, this, options);
    }
}

/// <summary>
/// SQS endpoint — owns the shared <see cref="IAmazonSQS"/> client and resolves the queue URL
/// (creating the queue on demand when <c>autoCreateQueue=true</c>).
/// </summary>
public sealed class SqsEndpoint : EndpointBase<SqsEndpointOptions>, IDisposable
{
    private IAmazonSQS? _client;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private string? _queueUrl;
    private readonly SemaphoreSlim _urlLock = new(1, 1);

    /// <summary>Queue name parsed from the URI path.</summary>
    public string QueueName { get; }

    /// <summary>True when the queue is FIFO (the name or an explicit queue URL ends in <c>.fifo</c>).</summary>
    public bool IsFifo => QueueName.EndsWith(".fifo", StringComparison.Ordinal)
        || Options.QueueUrl.EndsWith(".fifo", StringComparison.Ordinal);

    /// <summary>Logger from the parent component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Typed options (exposed for consumer/producer).</summary>
    internal SqsEndpointOptions EndpointOptions => Options;

    /// <summary>Creates an SQS endpoint.</summary>
    public SqsEndpoint(EndpointUri uri, SqsComponent component, SqsEndpointOptions options)
        : base(uri, component, options)
    {
        Logger = component.Logger;
        QueueName = uri.Path.TrimStart('/');
        if (string.IsNullOrEmpty(QueueName) && string.IsNullOrEmpty(options.QueueUrl))
            throw new ArgumentException("SQS URI must include a queue name in the path (sqs://queue-name).");
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new SqsProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new SqsConsumer(this, processor, Options);
    }

    /// <summary>Gets or creates the shared <see cref="IAmazonSQS"/> client.</summary>
    internal async Task<IAmazonSQS> GetOrCreateClientAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;

            if (!string.IsNullOrEmpty(Options.ConnectionFactory)
                && (Component as SqsComponent)?.Context?.GetFromRegistry<AwsConnectionFactory>(Options.ConnectionFactory) is { } factory)
            {
                _client = factory.BuildSqs();
            }
            else
            {
                var config = new AmazonSQSConfig();
                AwsClientSupport.ApplyCommonConfig(config, Options);
                _client = new AmazonSQSClient(AwsClientSupport.ResolveCredentials(Options), config);
            }

            Logger?.LogInformation("SQS client created: Region={Region}, ServiceUrl={ServiceUrl}, Queue={Queue}",
                Options.Region, string.IsNullOrEmpty(Options.ServiceUrl) ? "(default)" : Options.ServiceUrl, QueueName);
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>Resolves (and caches) the queue URL, creating the queue first when <c>autoCreateQueue=true</c>.</summary>
    internal async Task<string> GetQueueUrlAsync(CancellationToken ct = default)
    {
        if (_queueUrl is not null) return _queueUrl;

        await _urlLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_queueUrl is not null) return _queueUrl;

            if (!string.IsNullOrEmpty(Options.QueueUrl))
                return _queueUrl = Options.QueueUrl;

            var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);

            if (Options.AutoCreateQueue)
            {
                var request = new CreateQueueRequest { QueueName = QueueName };
                if (IsFifo)
                {
                    // AWS SDK v4 does not initialize request collections — assign the dictionary.
                    request.Attributes = new Dictionary<string, string>
                    {
                        ["FifoQueue"] = "true",
                        ["ContentBasedDeduplication"] = "true",
                    };
                }
                var created = await client.CreateQueueAsync(request, ct).ConfigureAwait(false);
                return _queueUrl = created.QueueUrl;
            }

            var resp = await client.GetQueueUrlAsync(QueueName, ct).ConfigureAwait(false);
            return _queueUrl = resp.QueueUrl;
        }
        finally
        {
            _urlLock.Release();
        }
    }

    /// <inheritdoc />
    public override async Task Stop(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            try { _client.Dispose(); }
            catch (Exception ex) { Logger?.LogWarning(ex, "Error disposing SQS client"); }
            _client = null;
        }
        Logger?.LogInformation("SQS endpoint stopped: {Uri}", Uri);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _clientLock.Dispose();
        _urlLock.Dispose();
    }
}
