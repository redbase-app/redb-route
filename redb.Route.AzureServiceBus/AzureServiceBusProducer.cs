using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// Sends messages to an Azure Service Bus queue or topic.
/// Supports single send, batch send, and scheduled delivery.
/// </summary>
internal sealed class AzureServiceBusProducer : ConnectableProducer
{
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly AzureServiceBusEndpointOptions _options;
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

        if (_options.EnableBatch)
            await SendBatchAsync(exchange, ct).ConfigureAwait(false);
        else
            await SendSingleAsync(exchange, ct).ConfigureAwait(false);
    }

    // ── Single send ──

    private async Task SendSingleAsync(IExchange exchange, CancellationToken ct)
    {
        var msg = BuildMessage(exchange);

        await _sender!.SendMessageAsync(msg, ct).ConfigureAwait(false);

        exchange.In.Headers[AzureServiceBusHeaders.MessageId] = msg.MessageId;
    }

    // ── Batch send ──

    private async Task SendBatchAsync(IExchange exchange, CancellationToken ct)
    {
        var body = exchange.In.Body
            ?? throw new InvalidOperationException("Body is null; batch send requires an IEnumerable body.");

        if (body is not IEnumerable items)
            throw new InvalidOperationException(
                $"Body type {body.GetType().Name} is not IEnumerable; batch send requires a collection.");

        using var batch = await _sender!.CreateMessageBatchAsync(
            new CreateMessageBatchOptions { MaxSizeInBytes = _options.BatchMaxSizeBytes }, ct)
            .ConfigureAwait(false);

        var count = 0;
        foreach (var item in items)
        {
            if (count >= _options.BatchMaxMessages) break;

            var data = ResolveBodyData(item);
            var msg = new ServiceBusMessage(data);
            ApplyProperties(msg, exchange);
            msg.MessageId = Guid.NewGuid().ToString(); // unique id per batched message

            if (!batch.TryAddMessage(msg))
                break;

            count++;
        }

        if (batch.Count > 0)
            await _sender.SendMessagesAsync(batch, ct).ConfigureAwait(false);

        exchange.In.Headers[AzureServiceBusHeaders.BatchMessageCount] = batch.Count;
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
