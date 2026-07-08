using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Sqs;

/// <summary>
/// Amazon SNS transport component for redb.Route. Scheme: <c>sns</c>.
/// <para>Publisher: <c>sns://my-topic?region=us-east-1</c> — publishes the exchange body to the topic.</para>
/// <para>SNS has no pull consumer; consume a topic by subscribing an SQS queue to it (see
/// <c>subscribeSnsToSqs</c>) and reading with the <c>sqs://</c> connector.</para>
/// </summary>
public sealed class SnsComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "sns";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new SnsEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new SnsEndpoint(uri, this, options);
    }
}

/// <summary>
/// SNS endpoint — owns the shared <see cref="IAmazonSimpleNotificationService"/> client and resolves
/// the topic ARN (creating the topic on demand when <c>autoCreateTopic=true</c>). Producer-only.
/// </summary>
public sealed class SnsEndpoint : EndpointBase<SnsEndpointOptions>, IDisposable
{
    private IAmazonSimpleNotificationService? _client;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private string? _topicArn;
    private readonly SemaphoreSlim _arnLock = new(1, 1);

    /// <summary>Topic name parsed from the URI path.</summary>
    public string TopicName { get; }

    /// <summary>True when the topic is FIFO (the name or an explicit topic ARN ends in <c>.fifo</c>).</summary>
    public bool IsFifo => TopicName.EndsWith(".fifo", StringComparison.Ordinal)
        || Options.TopicArn.EndsWith(".fifo", StringComparison.Ordinal);

    /// <summary>Logger from the parent component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Typed options.</summary>
    internal SnsEndpointOptions EndpointOptions => Options;

    /// <summary>Creates an SNS endpoint.</summary>
    public SnsEndpoint(EndpointUri uri, SnsComponent component, SnsEndpointOptions options)
        : base(uri, component, options)
    {
        Logger = component.Logger;
        TopicName = uri.Path.TrimStart('/');
        if (string.IsNullOrEmpty(TopicName) && string.IsNullOrEmpty(options.TopicArn))
            throw new ArgumentException("SNS URI must include a topic name in the path (sns://topic-name).");
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new SnsProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor) =>
        throw new NotSupportedException(
            "SNS is publish-only — it has no pull consumer. To consume an SNS topic, subscribe an SQS "
            + "queue to it (sns://topic?subscribeSnsToSqs=true&subscribeQueueArn=...) and read with sqs://.");

    /// <summary>Gets or creates the shared <see cref="IAmazonSimpleNotificationService"/> client.</summary>
    internal async Task<IAmazonSimpleNotificationService> GetOrCreateClientAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;

            if (!string.IsNullOrEmpty(Options.ConnectionFactory)
                && (Component as SnsComponent)?.Context?.GetFromRegistry<AwsConnectionFactory>(Options.ConnectionFactory) is { } factory)
            {
                _client = factory.BuildSns();
            }
            else
            {
                var config = new AmazonSimpleNotificationServiceConfig();
                AwsClientSupport.ApplyCommonConfig(config, Options);
                _client = new AmazonSimpleNotificationServiceClient(AwsClientSupport.ResolveCredentials(Options), config);
            }

            Logger?.LogInformation("SNS client created: Region={Region}, ServiceUrl={ServiceUrl}, Topic={Topic}",
                Options.Region, string.IsNullOrEmpty(Options.ServiceUrl) ? "(default)" : Options.ServiceUrl, TopicName);
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>Resolves (and caches) the topic ARN, creating the topic first when <c>autoCreateTopic=true</c>.</summary>
    internal async Task<string> GetTopicArnAsync(CancellationToken ct = default)
    {
        if (_topicArn is not null) return _topicArn;

        await _arnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_topicArn is not null) return _topicArn;

            if (!string.IsNullOrEmpty(Options.TopicArn))
                return _topicArn = Options.TopicArn;

            var client = await GetOrCreateClientAsync(ct).ConfigureAwait(false);

            if (Options.AutoCreateTopic)
            {
                var request = new CreateTopicRequest { Name = TopicName };
                if (IsFifo)
                {
                    // AWS SDK v4 does not initialize request collections — assign the dictionary.
                    request.Attributes = new Dictionary<string, string>
                    {
                        ["FifoTopic"] = "true",
                        ["ContentBasedDeduplication"] = "true",
                    };
                }
                var created = await client.CreateTopicAsync(request, ct).ConfigureAwait(false);
                return _topicArn = created.TopicArn;
            }

            // No ARN and no auto-create: build the conventional ARN is not reliable — require an explicit ARN.
            throw new InvalidOperationException(
                $"SNS topic ARN unknown for '{TopicName}'. Set topicArn=... or autoCreateTopic=true.");
        }
        finally
        {
            _arnLock.Release();
        }
    }

    /// <inheritdoc />
    public override async Task Stop(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            try { _client.Dispose(); }
            catch (Exception ex) { Logger?.LogWarning(ex, "Error disposing SNS client"); }
            _client = null;
        }
        Logger?.LogInformation("SNS endpoint stopped: {Uri}", Uri);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _clientLock.Dispose();
        _arnLock.Dispose();
    }
}
