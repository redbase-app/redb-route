using System.Diagnostics;
using System.Globalization;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Sqs;

/// <summary>
/// SNS producer — publishes the exchange body to the topic. User headers become message attributes;
/// FIFO group/dedup ids resolve from headers or options. When <c>subscribeSnsToSqs=true</c> it
/// subscribes the configured SQS queue to the topic on start (a convenience for the SNS→SQS pattern).
/// </summary>
internal sealed class SnsProducer : ConnectableProducer
{
    private readonly SnsEndpoint _endpoint;
    private readonly SnsEndpointOptions _options;
    private IAmazonSimpleNotificationService? _client;
    private string _topicArn = "";

    internal SnsProducer(SnsEndpoint endpoint, SnsEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"sns://{_endpoint.TopicName}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _client = await _endpoint.GetOrCreateClientAsync(ct).ConfigureAwait(false);
        _topicArn = await _endpoint.GetTopicArnAsync(ct).ConfigureAwait(false);

        if (_options.SubscribeSnsToSqs && !string.IsNullOrEmpty(_options.SubscribeQueueArn))
        {
            await _client.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = _topicArn,
                Protocol = "sqs",
                Endpoint = _options.SubscribeQueueArn,
                ReturnSubscriptionArn = true,
            }, ct).ConfigureAwait(false);
            Logger?.LogInformation("SNS: subscribed SQS {QueueArn} to topic {Topic}", _options.SubscribeQueueArn, _topicArn);
        }
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"{_endpoint.TopicName} publish", ActivityKind.Producer,
            "messaging.system", "sns",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.TopicName,
            operation: "publish");

        var request = new PublishRequest
        {
            TopicArn = _topicArn,
            Message = AwsClientSupport.ResolveTextBody(exchange.In.Body),
            MessageAttributes = BuildAttributes(exchange),
        };

        var subject = ResolveHeader(exchange, SnsHeaders.Subject) ?? _options.ResolveOption(_options.Subject, exchange);
        if (!string.IsNullOrEmpty(subject))
            request.Subject = subject;

        if (!string.IsNullOrEmpty(_options.MessageStructure))
            request.MessageStructure = _options.MessageStructure;

        if (_endpoint.IsFifo)
        {
            request.MessageGroupId = ResolveGroupId(exchange) ?? throw new InvalidOperationException(
                "FIFO topic requires a messageGroupId (set the option or the redbSns.messageGroupId header).");
            var dedup = ResolveHeader(exchange, SnsHeaders.MessageDeduplicationId)
                        ?? _options.MessageDeduplicationId?.Resolve(exchange);
            if (!string.IsNullOrEmpty(dedup))
                request.MessageDeduplicationId = dedup;
        }

        var response = await _client!.PublishAsync(request, ct).ConfigureAwait(false);
        exchange.In.Headers[SnsHeaders.MessageId] = response.MessageId;
        if (!string.IsNullOrEmpty(response.SequenceNumber))
            exchange.In.Headers[SnsHeaders.SequenceNumber] = response.SequenceNumber;

        _endpoint.RecordMessageOut();
    }

    private string? ResolveGroupId(IExchange exchange) =>
        ResolveHeader(exchange, SnsHeaders.MessageGroupId) ?? _options.MessageGroupId?.Resolve(exchange);

    private static string? ResolveHeader(IExchange exchange, string key) =>
        exchange.In.Headers.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;

    private static Dictionary<string, MessageAttributeValue> BuildAttributes(IExchange exchange)
    {
        var attrs = new Dictionary<string, MessageAttributeValue>();
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (value is null) continue;
            var name = AwsClientSupport.MapHeaderToAttributeName(key);
            if (name is null) continue;
            attrs[name] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            };
        }
        InjectTraceContext(attrs);
        return attrs;
    }

    /// <summary>Injects <c>traceparent</c>/<c>tracestate</c> from the current <see cref="Activity"/> as attributes.</summary>
    private static void InjectTraceContext(Dictionary<string, MessageAttributeValue> attrs)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        DistributedContextPropagator.Current.Inject(activity, attrs, static (carrier, key, value) =>
            ((Dictionary<string, MessageAttributeValue>)carrier!)[key] =
                new MessageAttributeValue { DataType = "String", StringValue = value });
    }
}
