using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// Event-driven consumer using <see cref="ServiceBusProcessor"/> callback model.
/// Supports PeekLock with manual Complete/Abandon/DeadLetter and ReceiveAndDelete.
/// </summary>
internal sealed class AzureServiceBusConsumer : IConsumer
{
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly IProcessor _pipeline;
    private readonly AzureServiceBusEndpointOptions _options;
    private readonly InflightDrainGuard _drain = new();

    private ServiceBusProcessor? _processor;
    private ILogger? _logger;

    public IEndpoint? Endpoint => _endpoint;

    internal AzureServiceBusConsumer(AzureServiceBusEndpoint endpoint, IProcessor pipeline,
        AzureServiceBusEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(options);

        _endpoint = endpoint;
        _pipeline = pipeline;
        _options = options;
    }

    public async Task Start(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;

        var client = await _endpoint.GetOrCreateClientAsync(ct).ConfigureAwait(false);

        var processorOptions = new ServiceBusProcessorOptions
        {
            ReceiveMode = _options.ParsedReceiveMode,
            MaxConcurrentCalls = _options.MaxConcurrentCalls,
            PrefetchCount = _options.PrefetchCount,
            MaxAutoLockRenewalDuration = TimeSpan.FromSeconds(_options.MaxAutoLockRenewalDuration),
            AutoCompleteMessages = false
        };

        if (_options.ParsedSubQueue is not null)
            processorOptions.SubQueue = _options.ParsedSubQueue.Value;

        _processor = _endpoint.IsTopic
            ? client.CreateProcessor(_endpoint.EntityName, _options.SubscriptionName, processorOptions)
            : client.CreateProcessor(_endpoint.EntityName, processorOptions);

        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += OnErrorAsync;

        _drain.Start(ct);
        await _processor.StartProcessingAsync(ct).ConfigureAwait(false);

        _logger?.LogInformation("ASB consumer started: entity={Entity}, receiveMode={Mode}, concurrent={Concurrent}",
            _endpoint.EntityName, _options.ReceiveMode, _options.MaxConcurrentCalls);
    }

    public async Task Stop(CancellationToken ct = default)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(ct).ConfigureAwait(false);
        }

        await _drain.DrainAsync(ct, _logger, $"asb:{_endpoint.EntityName}").ConfigureAwait(false);
        _drain.Dispose();

        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            _processor = null;
        }

        _logger?.LogInformation("ASB consumer stopped: entity={Entity}", _endpoint.EntityName);
    }

    // ── Message handling ──

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        _drain.Increment();
        Exchange? exchange = null;
        try
        {
            exchange = CreateExchange(args.Message);

            if (_options.Transacted)
            {
                RegisterTransactedAction(exchange, $"asb-ack-{args.Message.SequenceNumber}",
                    new AzureServiceBusAckAction(args));
            }

            await _pipeline.Process(exchange, args.CancellationToken).ConfigureAwait(false);

            // Acknowledge (PeekLock + non-transacted only)
            if (_options.ParsedReceiveMode == ServiceBusReceiveMode.PeekLock && !_options.Transacted)
            {
                await AcknowledgeAsync(args, exchange).ConfigureAwait(false);
            }

            _endpoint.RecordMessageIn();
        }
        catch (Exception ex)
        {
            if (_options.ParsedReceiveMode == ServiceBusReceiveMode.PeekLock)
            {
                try
                {
                    await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception abandonEx)
                {
                    _logger?.LogWarning(abandonEx, "Failed to abandon ASB message");
                }
            }

            _endpoint.RecordError(ex);
            throw;
        }
        finally
        {
            if (exchange is not null)
                await exchange.DisposeAsync().ConfigureAwait(false);
            _drain.Decrement();
        }
    }

    private async Task AcknowledgeAsync(ProcessMessageEventArgs args, Exchange exchange)
    {
        if (exchange.Exception is not null && !exchange.ExceptionHandled)
        {
            if (_options.AutoDeadLetter)
            {
                await args.DeadLetterMessageAsync(args.Message,
                    _options.DeadLetterReason ?? "ProcessingError",
                    exchange.Exception.Message,
                    cancellationToken: args.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            await args.CompleteMessageAsync(args.Message, cancellationToken: args.CancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        _endpoint.RecordError(args.Exception);
        _logger?.LogError(args.Exception, "ASB error on {EntityPath}: {Source}",
            args.EntityPath, args.ErrorSource);
        return Task.CompletedTask;
    }

    // ── Exchange creation ──

    internal Exchange CreateExchange(ServiceBusReceivedMessage msg)
    {
        var message = new Message(msg.Body.ToArray());

        // Identity
        SetIfNotEmpty(message, AzureServiceBusHeaders.MessageId, msg.MessageId);
        SetIfNotEmpty(message, AzureServiceBusHeaders.CorrelationId, msg.CorrelationId);
        SetIfNotEmpty(message, AzureServiceBusHeaders.SessionId, msg.SessionId);
        SetIfNotEmpty(message, AzureServiceBusHeaders.PartitionKey, msg.PartitionKey);
        SetIfNotEmpty(message, AzureServiceBusHeaders.ReplyToSessionId, msg.ReplyToSessionId);

        // Metadata
        SetIfNotEmpty(message, AzureServiceBusHeaders.Subject, msg.Subject);
        SetIfNotEmpty(message, AzureServiceBusHeaders.ContentType, msg.ContentType);
        SetIfNotEmpty(message, AzureServiceBusHeaders.ReplyTo, msg.ReplyTo);
        SetIfNotEmpty(message, AzureServiceBusHeaders.To, msg.To);

        if (msg.ContentType is not null)
            message.ContentType = msg.ContentType;

        message.Headers[AzureServiceBusHeaders.TimeToLive] = msg.TimeToLive;

        // Scheduling
        message.Headers[AzureServiceBusHeaders.ScheduledEnqueueTime] = msg.ScheduledEnqueueTime;
        message.Headers[AzureServiceBusHeaders.SequenceNumber] = msg.SequenceNumber;

        // Consumer metadata
        message.Headers[AzureServiceBusHeaders.DeliveryCount] = msg.DeliveryCount;
        message.Headers[AzureServiceBusHeaders.EnqueuedTime] = msg.EnqueuedTime;
        message.Headers[AzureServiceBusHeaders.ExpiresAt] = msg.ExpiresAt;

        if (_options.ParsedReceiveMode == ServiceBusReceiveMode.PeekLock)
            message.Headers[AzureServiceBusHeaders.LockedUntil] = msg.LockedUntil;

        // Dead-letter metadata
        SetIfNotEmpty(message, AzureServiceBusHeaders.DeadLetterSource, msg.DeadLetterSource);
        SetIfNotEmpty(message, AzureServiceBusHeaders.DeadLetterReason, msg.DeadLetterReason);
        SetIfNotEmpty(message, AzureServiceBusHeaders.DeadLetterErrorDescription, msg.DeadLetterErrorDescription);

        // Application properties → exchange headers
        foreach (var kv in msg.ApplicationProperties)
        {
            if (kv.Value is not null)
                message.Headers[kv.Key] = kv.Value;
        }

        return Exchange.Create(message, _endpoint.ScopeFactory);
    }

    private static void SetIfNotEmpty(IMessage message, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            message.Headers[key] = value;
    }

    private static void RegisterTransactedAction(IExchange exchange, string key, ITransactedAction action)
    {
        if (!exchange.Properties.TryGetValue("TRANSACT_ACTION", out var raw)
            || raw is not ConcurrentDictionary<string, ITransactedAction> dict)
        {
            dict = new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase);
            exchange.Properties["TRANSACT_ACTION"] = dict;
        }

        dict[key] = action;
    }
}
