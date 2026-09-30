using System.Diagnostics;
using System.Globalization;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Transactions;

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
            var subscription = await _client.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = _topicArn,
                Protocol = "sqs",
                Endpoint = _options.SubscribeQueueArn,
                ReturnSubscriptionArn = true,
            }, ct).ConfigureAwait(false);
            Logger?.LogInformation("SNS: subscribed SQS {QueueArn} to topic {Topic}", _options.SubscribeQueueArn, _topicArn);

            // Raw delivery: the queue gets the bare payload (and SNS attributes become SQS attributes)
            // instead of the default JSON notification envelope. Set it on the subscription we just made.
            if (_options.RawMessageDelivery && !string.IsNullOrEmpty(subscription.SubscriptionArn))
            {
                await _client.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
                {
                    SubscriptionArn = subscription.SubscriptionArn,
                    AttributeName = "RawMessageDelivery",
                    AttributeValue = "true",
                }, ct).ConfigureAwait(false);
                Logger?.LogInformation("SNS: enabled RawMessageDelivery on subscription {Sub}", subscription.SubscriptionArn);
            }
        }
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            (_endpoint.Component as ComponentBase)?.Context,
            $"{_endpoint.TopicName} publish", ActivityKind.Producer,
            "messaging.system", "sns",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.TopicName,
            operation: "publish");

        var request = new PublishRequest
        {
            TopicArn = _topicArn,
            Message = AwsClientSupport.ResolveTextBody(exchange.In.Body),
            MessageAttributes = BuildAttributes(exchange, activity),
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

        async Task Publish(CancellationToken token)
        {
            var response = await _client!.PublishAsync(request, token).ConfigureAwait(false);
            exchange.In.Headers[SnsHeaders.MessageId] = response.MessageId;
            if (!string.IsNullOrEmpty(response.SequenceNumber))
                exchange.In.Headers[SnsHeaders.SequenceNumber] = response.SequenceNumber;
        }

        // The request is built now, from the exchange as it is at this step; only the publish itself waits for the
        // commit when the producer joins the enclosing .Transacted() block.
        if (TransactedActions.Defers(exchange, _options.Transacted))
        {
            TransactedActions.RegisterSend(exchange, $"sns-publish-{Guid.NewGuid():N}", Publish, ProducerName);
            return;
        }

        try
        {
            await Publish(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only our own token cancelling the publish is a stop; a timeout or any other failure marks the span.
            activity.RecordFailure(ex);
            throw;
        }
        // No RecordMessageOut: the core (ToProcessor / the template) owns it (ownership audit).
    }

    private string? ResolveGroupId(IExchange exchange) =>
        ResolveHeader(exchange, SnsHeaders.MessageGroupId) ?? _options.MessageGroupId?.Resolve(exchange);

    private static string? ResolveHeader(IExchange exchange, string key) =>
        exchange.In.Headers.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;

    /// <summary>
    /// Maps forwardable headers to message attributes and adds the trace context of <paramref name="activity"/> (the
    /// publish span; without one, the ambient context), replacing a traceparent copied from a received message.
    /// </summary>
    private Dictionary<string, MessageAttributeValue> BuildAttributes(IExchange exchange, Activity? activity)
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
        var fromHeaders = attrs.Count;
        RouteTelemetryExtensions.InjectTraceContext(activity, attrs, static (a, name, value) =>
            a[name] = new MessageAttributeValue { DataType = "String", StringValue = value });
        AwsClientSupport.EnsureAttributeLimit(attrs.Count, fromHeaders, ProducerName);
        return attrs;
    }
}
