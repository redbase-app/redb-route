using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Transactions;

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
            (_endpoint.Component as ComponentBase)?.Context,
            $"{_endpoint.QueueName} send", ActivityKind.Producer,
            "messaging.system", "sqs",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.QueueName,
            operation: "send");

        // No RecordMessageOut: the core (ToProcessor / the template) owns it (ownership audit).
        // The requests are built now, from the exchange as it is at this step; only the send itself waits for the
        // commit when the producer joins the enclosing .Transacted() block.
        var send = _options.EnableBatch && exchange.In.Body is IEnumerable and not string and not byte[]
            ? PrepareBatch(exchange, activity)
            : PrepareSingle(exchange, activity);

        if (TransactedActions.Defers(exchange, _options.Transacted))
        {
            TransactedActions.RegisterSend(exchange, $"sqs-send-{Guid.NewGuid():N}", send, ProducerName);
            return;
        }

        try
        {
            await send(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only our own token cancelling the send is a stop; a timeout or any other failure marks the span.
            activity.RecordFailure(ex);
            throw;
        }
    }

    private Func<CancellationToken, Task> PrepareSingle(IExchange exchange, Activity? activity)
    {
        var request = new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = AwsClientSupport.ResolveTextBody(exchange.In.Body),
            MessageAttributes = BuildAttributes(exchange, activity),
        };

        if (_options.DelaySeconds > 0 && !_endpoint.IsFifo)
            request.DelaySeconds = _options.DelaySeconds; // FIFO queues reject per-message delay

        ApplyFifo(request, exchange);

        return async ct =>
        {
            var response = await _client!.SendMessageAsync(request, ct).ConfigureAwait(false);
            exchange.In.Headers[SqsHeaders.MessageId] = response.MessageId;
            if (!string.IsNullOrEmpty(response.SequenceNumber))
                exchange.In.Headers[SqsHeaders.SequenceNumber] = response.SequenceNumber;
        };
    }

    private Func<CancellationToken, Task> PrepareBatch(IExchange exchange, Activity? activity)
    {
        var requests = new List<SendMessageBatchRequest>();
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
                    MessageAttributes = BuildAttributes(exchange, activity),
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
            requests.Add(request);
        }

        return async ct =>
        {
            foreach (var request in requests)
                await _client!.SendMessageBatchAsync(request, ct).ConfigureAwait(false);
            exchange.In.Headers[SqsHeaders.Queue] = _endpoint.QueueName;
        };
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
    /// trace context of <paramref name="activity"/> (the send span; without one, the ambient context) so the
    /// downstream consumer can continue the distributed trace. A traceparent copied from a received message is replaced.
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
