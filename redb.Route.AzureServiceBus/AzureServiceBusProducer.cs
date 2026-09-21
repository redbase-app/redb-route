using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using System.Transactions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Transactions;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// Sends messages to an Azure Service Bus queue or topic.
/// Supports single send, batch send, and scheduled delivery.
/// </summary>
internal sealed class AzureServiceBusProducer : ConnectableProducer
{
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly AzureServiceBusEndpointOptions _options;
    private readonly string _batchKey = $"asb-batch-{Guid.NewGuid():N}";
    private ServiceBusSender? _sender;

    protected override IEndpoint ProducerEndpoint => _endpoint;
    protected override string ProducerName => _endpoint.Uri.NormalizedKey;

    internal AzureServiceBusProducer(AzureServiceBusEndpoint endpoint,
        AzureServiceBusEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    protected override async Task ConnectAsync(CancellationToken ct)
    {
        var client = await _endpoint.GetOrCreateClientAsync(ct).ConfigureAwait(false);
        _sender = client.CreateSender(_endpoint.EntityName);
    }

    protected override async Task DisconnectAsync(CancellationToken ct)
    {
        if (_sender is not null)
        {
            await _sender.DisposeAsync().ConfigureAwait(false);
            _sender = null;
        }
    }

    public override async Task Process(IExchange exchange, CancellationToken ct)
    {
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"{_endpoint.EntityName} send", ActivityKind.Producer,
            "messaging.system", "azureservicebus",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.EntityName,
            operation: "send");

        var defers = TransactedActions.Defers(exchange, _options.Transacted);
        if (defers && _options.BatchCommit)
        {
            // The block's sends through this producer leave as one Service Bus batch when the block commits.
            var messages = _options.EnableBatch ? BuildBatchMessages(exchange) : [BuildSingle(exchange)];
            if (_options.EnableBatch)
                exchange.In.Headers[AzureServiceBusHeaders.BatchMessageCount] = messages.Count;
            TransactedActions.JoinBatch(exchange, _batchKey, () => new AzureServiceBusSendBatch(this), ProducerName)
                .Add(messages);
            return;
        }

        // The messages are built now, from the exchange as it is at this step; only the send itself waits for the
        // commit when the producer joins the enclosing .Transacted() block.
        var send = _options.EnableBatch ? PrepareBatch(exchange) : PrepareSingle(exchange);

        if (defers)
        {
            TransactedActions.RegisterSend(exchange, $"asb-send-{Guid.NewGuid():N}", send, ProducerName);
            return;
        }

        // The Service Bus client enlists a send in the ambient System.Transactions transaction on its own. A send that
        // goes out at once must not: it would wait for the block's commit, and next to a database in the same block it
        // would escalate the transaction to a distributed one.
        using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            await send(ct).ConfigureAwait(false);
    }

    // ── Single send ──

    private Func<CancellationToken, Task> PrepareSingle(IExchange exchange)
    {
        var msg = BuildSingle(exchange);
        return ct => _sender!.SendMessageAsync(msg, ct);
    }

    private ServiceBusMessage BuildSingle(IExchange exchange)
    {
        var msg = BuildMessage(exchange);
        exchange.In.Headers[AzureServiceBusHeaders.MessageId] = msg.MessageId;
        return msg;
    }

    // ── Batch send ──

    private Func<CancellationToken, Task> PrepareBatch(IExchange exchange)
    {
        var messages = BuildBatchMessages(exchange);

        return async ct =>
        {
            await SendAsOneBatchAsync(messages, "this batch body", ct).ConfigureAwait(false);
            exchange.In.Headers[AzureServiceBusHeaders.BatchMessageCount] = messages.Count;
        };
    }

    /// <summary>
    /// One message per item of the collection body. More items than <c>batchMaxMessages</c> fail the step: they used to
    /// be dropped without a word.
    /// </summary>
    private List<ServiceBusMessage> BuildBatchMessages(IExchange exchange)
    {
        var body = exchange.In.Body
            ?? throw new InvalidOperationException("Body is null; batch send requires an IEnumerable body.");

        if (body is not IEnumerable items)
            throw new InvalidOperationException(
                $"Body type {body.GetType().Name} is not IEnumerable; batch send requires a collection.");

        var messages = new List<ServiceBusMessage>();
        foreach (var item in items)
        {
            if (messages.Count >= _options.BatchMaxMessages)
                throw new InvalidOperationException(
                    $"The batch body of '{ProducerName}' has more than batchMaxMessages={_options.BatchMaxMessages} items: " +
                    "nothing was sent. Split the body, or raise batchMaxMessages.");

            var data = ResolveBodyData(item);
            var msg = new ServiceBusMessage(data);
            ApplyProperties(msg, exchange);
            msg.MessageId = Guid.NewGuid().ToString(); // unique id per batched message
            messages.Add(msg);
        }
        return messages;
    }

    /// <summary>
    /// Sends <paramref name="messages"/> as one Service Bus batch: the entity takes it whole or not at all. Messages that
    /// do not fit in one batch fail the send before anything leaves: they used to be dropped without a word.
    /// </summary>
    internal async Task SendAsOneBatchAsync(IReadOnlyList<ServiceBusMessage> messages, string what, CancellationToken ct)
    {
        var sender = _sender ?? throw new InvalidOperationException(
            $"'{ProducerName}' is stopped: the messages cannot be sent.");
        using var batch = await sender.CreateMessageBatchAsync(
            new CreateMessageBatchOptions { MaxSizeInBytes = _options.BatchMaxSizeBytes }, ct).ConfigureAwait(false);

        foreach (var msg in messages)
        {
            if (!batch.TryAddMessage(msg))
                throw new InvalidOperationException(
                    $"The {messages.Count} messages of {what} do not fit in one Service Bus batch of {batch.MaxSizeInBytes} " +
                    $"bytes through '{ProducerName}': nothing was sent. Raise batchMaxSizeBytes up to the entity's limit, " +
                    "or send less at once.");
        }

        if (batch.Count > 0)
            await sender.SendMessagesAsync(batch, ct).ConfigureAwait(false);
    }

    // ── Message building ──

    private ServiceBusMessage BuildMessage(IExchange exchange)
    {
        var data = ResolveBodyData(exchange.In.Body
            ?? throw new InvalidOperationException("Body is null; cannot send an empty message."));

        var msg = new ServiceBusMessage(data);
        ApplyProperties(msg, exchange);
        return msg;
    }

    /// <summary>
    /// Maps native Service Bus properties and application properties from exchange headers/options.
    /// Shared by single and batch send so batched messages carry the same metadata.
    /// </summary>
    private void ApplyProperties(ServiceBusMessage msg, IExchange exchange)
    {
        // Identity
        msg.MessageId = ResolveHeader<string>(exchange, AzureServiceBusHeaders.MessageId)
            ?? _options.MessageId?.Resolve(exchange)
            ?? Guid.NewGuid().ToString();

        msg.CorrelationId = ResolveHeader<string>(exchange, AzureServiceBusHeaders.CorrelationId) ?? "";

        msg.SessionId = ResolveHeader<string>(exchange, AzureServiceBusHeaders.SessionId)
            ?? _options.ProducerSessionId?.Resolve(exchange)
            ?? "";

        var partitionKey = ResolveHeader<string>(exchange, AzureServiceBusHeaders.PartitionKey)
            ?? _options.PartitionKey;
        if (!string.IsNullOrEmpty(partitionKey) && string.IsNullOrEmpty(msg.SessionId))
            msg.PartitionKey = partitionKey;

        msg.ReplyToSessionId = ResolveHeader<string>(exchange, AzureServiceBusHeaders.ReplyToSessionId) ?? "";

        // Metadata
        msg.Subject = ResolveHeader<string>(exchange, AzureServiceBusHeaders.Subject) ?? "";
        msg.ContentType = ResolveHeader<string>(exchange, AzureServiceBusHeaders.ContentType)
            ?? exchange.In.ContentType ?? "";
        msg.ReplyTo = ResolveHeader<string>(exchange, AzureServiceBusHeaders.ReplyTo) ?? "";
        msg.To = ResolveHeader<string>(exchange, AzureServiceBusHeaders.To) ?? "";

        // TTL
        if (!string.IsNullOrEmpty(_options.TimeToLive))
            msg.TimeToLive = TimeSpan.Parse(_options.TimeToLive, System.Globalization.CultureInfo.InvariantCulture);

        if (ResolveHeader<string>(exchange, AzureServiceBusHeaders.TimeToLive) is { } ttlStr
            && !string.IsNullOrEmpty(ttlStr))
            msg.TimeToLive = TimeSpan.Parse(ttlStr, System.Globalization.CultureInfo.InvariantCulture);

        // Scheduling
        if (_options.ScheduleDelaySeconds > 0)
            msg.ScheduledEnqueueTime = DateTimeOffset.UtcNow.AddSeconds(_options.ScheduleDelaySeconds);

        if (exchange.In.GetHeader<DateTimeOffset?>(AzureServiceBusHeaders.ScheduledEnqueueTime)
            is { } scheduled)
            msg.ScheduledEnqueueTime = scheduled;

        // Application properties — copy non-ASB headers
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (AzureServiceBusHeaders.IsAsbHeader(key)) continue;
            if (value is null) continue;
            msg.ApplicationProperties[key] = value;
        }
    }

    private static BinaryData ResolveBodyData(object? body)
    {
        return body switch
        {
            byte[] bytes => BinaryData.FromBytes(bytes),
            BinaryData bd => bd,
            string str => BinaryData.FromString(str),
            Stream stream => BinaryData.FromStream(stream),
            _ => BinaryData.FromObjectAsJson(body)
        };
    }

    private static T? ResolveHeader<T>(IExchange exchange, string headerKey) where T : class
    {
        return exchange.In.GetHeader<T>(headerKey);
    }
}

/// <summary>
/// The messages a Service Bus producer with <c>batchCommit</c> deferred in one <c>.Transacted()</c> block, sent as one
/// batch when the block commits.
/// </summary>
internal sealed class AzureServiceBusSendBatch : ITransactedAction
{
    private readonly AzureServiceBusProducer _producer;
    private readonly System.Collections.Concurrent.ConcurrentQueue<ServiceBusMessage> _messages = new();

    public AzureServiceBusSendBatch(AzureServiceBusProducer producer) => _producer = producer;

    public void Add(IEnumerable<ServiceBusMessage> messages)
    {
        foreach (var message in messages)
            _messages.Enqueue(message);
    }

    public Task Commit(CancellationToken ct = default) =>
        _producer.SendAsOneBatchAsync(_messages.ToArray(), "this .Transacted() block", ct);

    // The block rolled back before anything was sent.
    public Task Rollback(CancellationToken ct = default) => Task.CompletedTask;
}
