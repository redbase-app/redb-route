using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// Session-aware consumer using <see cref="ServiceBusSessionProcessor"/>.
/// Sessions guarantee FIFO ordering per session ID and enable session state.
/// </summary>
internal sealed class AzureServiceBusSessionConsumer : IConsumer
{
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly IProcessor _pipeline;
    private readonly AzureServiceBusEndpointOptions _options;
    private readonly InflightDrainGuard _drain = new();

    private ServiceBusSessionProcessor? _processor;
    private ILogger? _logger;

    public IEndpoint? Endpoint => _endpoint;

    internal AzureServiceBusSessionConsumer(AzureServiceBusEndpoint endpoint, IProcessor pipeline,
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

        var processorOptions = new ServiceBusSessionProcessorOptions
        {
            ReceiveMode = _options.ParsedReceiveMode,
            MaxConcurrentSessions = _options.MaxConcurrentSessions,
            MaxConcurrentCallsPerSession = _options.MaxConcurrentCalls,
            PrefetchCount = _options.PrefetchCount,
            MaxAutoLockRenewalDuration = TimeSpan.FromSeconds(_options.MaxAutoLockRenewalDuration),
            AutoCompleteMessages = false
        };

        if (_options.SessionIdleTimeout > 0)
            processorOptions.SessionIdleTimeout = TimeSpan.FromSeconds(_options.SessionIdleTimeout);

        if (!string.IsNullOrEmpty(_options.SessionId))
            processorOptions.SessionIds.Add(_options.SessionId);

        _processor = _endpoint.IsTopic
            ? client.CreateSessionProcessor(_endpoint.EntityName, _options.SubscriptionName, processorOptions)
            : client.CreateSessionProcessor(_endpoint.EntityName, processorOptions);

        _processor.ProcessMessageAsync += OnSessionMessageAsync;
        _processor.ProcessErrorAsync += OnErrorAsync;

        _drain.Start(ct);
        await _processor.StartProcessingAsync(ct).ConfigureAwait(false);

        _logger?.LogInformation(
            "ASB session consumer started: entity={Entity}, sessions={MaxSessions}, callsPerSession={Calls}",
            _endpoint.EntityName, _options.MaxConcurrentSessions, _options.MaxConcurrentCalls);
    }

    public async Task Stop(CancellationToken ct = default)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(ct).ConfigureAwait(false);
        }

        await _drain.DrainAsync(ct, _logger, $"asb-session:{_endpoint.EntityName}").ConfigureAwait(false);
        _drain.Dispose();

        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            _processor = null;
        }

        _logger?.LogInformation("ASB session consumer stopped: entity={Entity}", _endpoint.EntityName);
    }

    // ── Session message handling ──

    private async Task OnSessionMessageAsync(ProcessSessionMessageEventArgs args)
    {
        _drain.Increment();
        Exchange? exchange = null;
        try
        {
            exchange = CreateExchange(args.Message, args);

            if (_options.Transacted)
            {
                RegisterTransactedAction(exchange, $"asb-session-ack-{args.Message.SequenceNumber}",
                    new AzureServiceBusSessionAckAction(args));
            }

            await _pipeline.Process(exchange, args.CancellationToken).ConfigureAwait(false);

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
                    _logger?.LogWarning(abandonEx, "Failed to abandon ASB session message");
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

    private async Task AcknowledgeAsync(ProcessSessionMessageEventArgs args, Exchange exchange)
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
        _logger?.LogError(args.Exception, "ASB session error on {EntityPath}: {Source}",
            args.EntityPath, args.ErrorSource);
        return Task.CompletedTask;
    }

    // ── Exchange creation ──

    private Exchange CreateExchange(ServiceBusReceivedMessage msg, ProcessSessionMessageEventArgs args)
    {
        var message = new Message(msg.Body.ToArray());

        // Identity
        SetIfNotEmpty(message, AzureServiceBusHeaders.MessageId, msg.MessageId);
        SetIfNotEmpty(message, AzureServiceBusHeaders.CorrelationId, msg.CorrelationId);
        SetIfNotEmpty(message, AzureServiceBusHeaders.PartitionKey, msg.PartitionKey);
        SetIfNotEmpty(message, AzureServiceBusHeaders.ReplyToSessionId, msg.ReplyToSessionId);

        // Session — always present for session consumer
        message.Headers[AzureServiceBusHeaders.SessionId] = args.SessionId;

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
