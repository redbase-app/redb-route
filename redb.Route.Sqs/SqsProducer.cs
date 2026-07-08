using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Sqs;

/// <summary>
/// SQS producer — sends the exchange body to the queue as a single message, or (when
/// <c>enableBatch=true</c> and the body is an <see cref="IEnumerable"/>) as a <c>SendMessageBatch</c>.
/// User headers become message attributes; FIFO group/dedup ids are resolved from headers or options.
/// </summary>
internal sealed class SqsProducer : ConnectableProducer
{
    private readonly SqsEndpoint _endpoint;
    private readonly SqsEndpointOptions _options;
    private IAmazonSQS? _client;
    private string _queueUrl = "";

    internal SqsProducer(SqsEndpoint endpoint, SqsEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"sqs://{_endpoint.QueueName}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _client = await _endpoint.GetOrCreateClientAsync(ct).ConfigureAwait(false);
        _queueUrl = await _endpoint.GetQueueUrlAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"{_endpoint.QueueName} send", ActivityKind.Producer,
            "messaging.system", "sqs",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.QueueName,
            operation: "send");

        if (_options.EnableBatch && exchange.In.Body is IEnumerable and not string and not byte[])
            await SendBatchAsync(exchange, ct).ConfigureAwait(false);
        else
            await SendSingleAsync(exchange, ct).ConfigureAwait(false);

        _endpoint.RecordMessageOut();
    }

    private async Task SendSingleAsync(IExchange exchange, CancellationToken ct)
    {
        var request = new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = AwsClientSupport.ResolveTextBody(exchange.In.Body),
            MessageAttributes = BuildAttributes(exchange),
        };

        if (_options.DelaySeconds > 0 && !_endpoint.IsFifo)
            request.DelaySeconds = _options.DelaySeconds; // FIFO queues reject per-message delay

        ApplyFifo(request, exchange);

        var response = await _client!.SendMessageAsync(request, ct).ConfigureAwait(false);
        exchange.In.Headers[SqsHeaders.MessageId] = response.MessageId;
        if (!string.IsNullOrEmpty(response.SequenceNumber))
            exchange.In.Headers[SqsHeaders.SequenceNumber] = response.SequenceNumber;
    }

    private async Task SendBatchAsync(IExchange exchange, CancellationToken ct)
    {
        var items = ((IEnumerable)exchange.In.Body!).Cast<object?>().ToList();
        var groupId = ResolveGroupId(exchange);
        var index = 0;

        foreach (var chunk in items.Chunk(_options.BatchMaxMessages))
        {
            // AWS SDK v4 does not initialize request collections — start with an empty entry list.
            var request = new SendMessageBatchRequest { QueueUrl = _queueUrl, Entries = [] };
            foreach (var item in chunk)
            {
                var entry = new SendMessageBatchRequestEntry
                {
                    Id = $"m{index++}",
                    MessageBody = AwsClientSupport.ResolveTextBody(item),
                    MessageAttributes = BuildAttributes(exchange),
                };
                if (_endpoint.IsFifo)
                {
                    entry.MessageGroupId = groupId ?? throw new InvalidOperationException(
                        "FIFO queue requires a messageGroupId (set the option or the redbSqs.messageGroupId header).");
                    var dedup = _options.MessageDeduplicationId?.Resolve(exchange);
                    if (!string.IsNullOrEmpty(dedup))
                        // The option resolves once per exchange; make it unique per entry so distinct
                        // batch bodies are not collapsed by FIFO deduplication.
                        entry.MessageDeduplicationId = $"{dedup}-{entry.Id}";
                }
                request.Entries.Add(entry);
            }
            await _client!.SendMessageBatchAsync(request, ct).ConfigureAwait(false);
        }

        exchange.In.Headers[SqsHeaders.Queue] = _endpoint.QueueName;
    }

    private void ApplyFifo(SendMessageRequest request, IExchange exchange)
    {
        if (!_endpoint.IsFifo) return;

        request.MessageGroupId = ResolveGroupId(exchange) ?? throw new InvalidOperationException(
            "FIFO queue requires a messageGroupId (set the option or the redbSqs.messageGroupId header).");

        var dedup = ResolveHeader(exchange, SqsHeaders.MessageDeduplicationId)
                    ?? _options.MessageDeduplicationId?.Resolve(exchange);
        if (!string.IsNullOrEmpty(dedup))
            request.MessageDeduplicationId = dedup;
    }

    private string? ResolveGroupId(IExchange exchange) =>
        ResolveHeader(exchange, SqsHeaders.MessageGroupId) ?? _options.MessageGroupId?.Resolve(exchange);

    private static string? ResolveHeader(IExchange exchange, string key) =>
        exchange.In.Headers.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;

    /// <summary>
    /// Maps forwardable headers to SQS message attributes (String values) and injects the current
    /// trace context so the downstream consumer can continue the distributed trace.
    /// </summary>
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
