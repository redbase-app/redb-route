using System.Collections.Concurrent;
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Transactions;
using RouteMessage = redb.Route.Core.Message;
using SqsMessage = Amazon.SQS.Model.Message;

namespace redb.Route.Sqs;

/// <summary>
/// SQS consumer — long-polls the queue and dispatches each message through the route pipeline.
/// Extends <see cref="DrainableConsumer"/> for two-CTS graceful shutdown. Runs
/// <c>concurrentConsumers</c> competing receive loops (the SQS-native concurrency model). A message is
/// deleted (acknowledged) only after it processes successfully; on failure it is left for redelivery
/// after the visibility timeout (or reset to 0 immediately when <c>resetVisibilityOnFailure=true</c>).
/// When <c>transacted=true</c> the delete is deferred into the route transaction via <see cref="SqsAckAction"/>.
/// </summary>
internal sealed class SqsConsumer : DrainableConsumer
{
    private readonly SqsEndpoint _endpoint;
    private readonly SqsEndpointOptions _options;
    private IAmazonSQS? _client;
    private string _queueUrl = "";
    private readonly List<string> _systemAttributeNames;
    private readonly List<string> _messageAttributeNames;

    protected override IEndpoint ConsumerEndpoint => _endpoint;
    protected override string ConsumerName => $"sqs://{_endpoint.QueueName}";

    internal SqsConsumer(SqsEndpoint endpoint, IProcessor processor, SqsEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint;
        _options = options;
        _systemAttributeNames = SplitCsv(options.AttributeNames);
        _messageAttributeNames = SplitCsv(options.MessageAttributeNames);
    }

    /// <inheritdoc />
    protected override async Task OnStarting(CancellationToken ct)
    {
        _client = await _endpoint.GetOrCreateClientAsync(ct).ConfigureAwait(false);
        _queueUrl = await _endpoint.GetQueueUrlAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        // N competing receive loops — the SQS-native concurrency model (multiple receivers on one queue).
        var workers = new Task[_options.ConcurrentConsumers];
        for (var i = 0; i < _options.ConcurrentConsumers; i++)
            workers[i] = Task.Run(() => WorkerLoop(pollCt, processingCt), CancellationToken.None);
        return Task.WhenAll(workers);
    }

    private async Task WorkerLoop(CancellationToken pollCt, CancellationToken processingCt)
    {
        if (_options.InitialDelay > 0)
        {
            try { await Task.Delay(_options.InitialDelay, pollCt).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                var messages = await ReceiveAsync(pollCt).ConfigureAwait(false);
                if (messages.Count == 0)
                {
                    if (_options.Delay > 0)
                        await Task.Delay(_options.Delay, pollCt).ConfigureAwait(false);
                    continue;
                }

                foreach (var msg in messages)
                {
                    if (processingCt.IsCancellationRequested) break;
                    await HandleMessageAsync(msg, processingCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "SQS receive error on queue {Queue}", _endpoint.QueueName);
                _endpoint.RecordError();
                try { await Task.Delay(1000, pollCt).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<List<SqsMessage>> ReceiveAsync(CancellationToken ct)
    {
        var request = new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = _options.MaxNumberOfMessages,
            WaitTimeSeconds = _options.WaitTimeSeconds,
            MessageSystemAttributeNames = _systemAttributeNames,
            MessageAttributeNames = _messageAttributeNames,
        };
        if (_options.VisibilityTimeout > 0)
            request.VisibilityTimeout = _options.VisibilityTimeout;

        var response = await _client!.ReceiveMessageAsync(request, ct).ConfigureAwait(false);
        return response.Messages ?? [];
    }

    private async Task HandleMessageAsync(SqsMessage msg, CancellationToken processingCt)
    {
        using var activity = StartConsumerActivity(msg);

        IncrementInflight();
        IExchange? exchange = null;
        IDisposable? heartbeat = null;
        try
        {
            exchange = CreateExchange(msg);

            if (_options.Transacted)
                RegisterTransactedAction(exchange, $"sqs-ack-{msg.MessageId}",
                    new SqsAckAction(_client!, _queueUrl, msg.ReceiptHandle, _options.DeleteAfterRead));

            heartbeat = StartVisibilityHeartbeat(msg.ReceiptHandle, processingCt);

            _endpoint.RecordMessageIn();
            await Processor.Process(exchange, processingCt).ConfigureAwait(false);

            // Stop the heartbeat BEFORE acknowledging so it can never re-hide a message that is about
            // to be deleted, nor undo a rollback's visibility reset (transacted fast-retry).
            heartbeat?.Dispose();
            heartbeat = null;

            var failed = exchange.Exception is not null && !exchange.ExceptionHandled;

            // Transacted mode settles via TransactedProcessor (SqsAckAction). Otherwise ack here.
            if (!_options.Transacted)
            {
                if (!failed && _options.DeleteAfterRead)
                    await _client!.DeleteMessageAsync(_queueUrl, msg.ReceiptHandle, processingCt).ConfigureAwait(false);
                else if (failed && _options.ResetVisibilityOnFailure)
                    await SafeResetVisibilityAsync(msg.ReceiptHandle).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (processingCt.IsCancellationRequested)
        {
            // Graceful shutdown — leave the message for redelivery (do not delete).
        }
        catch (Exception ex)
        {
            // Any other failure (incl. a downstream TaskCanceledException that is NOT our shutdown token).
            _endpoint.RecordError();
            Logger?.LogError(ex, "SQS message processing failed on queue {Queue}, messageId={MessageId}",
                _endpoint.QueueName, msg.MessageId);
            if (!_options.Transacted && _options.ResetVisibilityOnFailure)
                await SafeResetVisibilityAsync(msg.ReceiptHandle).ConfigureAwait(false);
        }
        finally
        {
            heartbeat?.Dispose();
            DecrementInflight();
            if (exchange is not null)
                await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SafeResetVisibilityAsync(string receiptHandle)
    {
        try
        {
            await _client!.ChangeMessageVisibilityAsync(_queueUrl, receiptHandle, 0, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "SQS: failed to reset visibility on {Queue}", _endpoint.QueueName);
        }
    }

    // ── Exchange + telemetry ──────────────────────────────────────────

    private IExchange CreateExchange(SqsMessage msg)
    {
        var message = new RouteMessage { Body = msg.Body };
        var headers = message.Headers;
        headers[SqsHeaders.Queue] = _endpoint.QueueName;
        headers[SqsHeaders.MessageId] = msg.MessageId;
        headers[SqsHeaders.ReceiptHandle] = msg.ReceiptHandle;
        if (!string.IsNullOrEmpty(msg.MD5OfBody))
            headers[SqsHeaders.Md5OfBody] = msg.MD5OfBody;
        headers[SqsHeaders.Timestamp] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (msg.Attributes is not null)
        {
            if (msg.Attributes.TryGetValue("ApproximateReceiveCount", out var arc))
                headers[SqsHeaders.ApproximateReceiveCount] = arc;
            if (msg.Attributes.TryGetValue("SentTimestamp", out var st))
                headers[SqsHeaders.SentTimestamp] = st;
            if (msg.Attributes.TryGetValue("MessageGroupId", out var gid))
                headers[SqsHeaders.MessageGroupId] = gid;
            if (msg.Attributes.TryGetValue("SequenceNumber", out var seq))
                headers[SqsHeaders.SequenceNumber] = seq;
        }

        if (msg.MessageAttributes is not null)
            foreach (var (name, value) in msg.MessageAttributes)
                headers[SqsHeaders.MessageAttributePrefix + name] = value.StringValue;

        return Exchange.Create(message, _endpoint.ScopeFactory);
    }

    private Activity? StartConsumerActivity(SqsMessage msg)
    {
        ActivityContext parentContext = default;
        if (msg.MessageAttributes is not null
            && msg.MessageAttributes.TryGetValue("traceparent", out var tp)
            && !string.IsNullOrEmpty(tp.StringValue))
        {
            var traceState = msg.MessageAttributes.TryGetValue("tracestate", out var ts) ? ts.StringValue : null;
            ActivityContext.TryParse(tp.StringValue, traceState, out parentContext);
        }

        var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.QueueName} receive", ActivityKind.Consumer, parentContext);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "sqs");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", _endpoint.QueueName);
            activity.SetTag("messaging.message.id", msg.MessageId);
        }
        return activity;
    }

    // ── Visibility heartbeat ──────────────────────────────────────────

    private IDisposable? StartVisibilityHeartbeat(string receiptHandle, CancellationToken ct)
    {
        if (!_options.ExtendMessageVisibility || _options.VisibilityTimeout <= 0)
            return null;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.VisibilityTimeout / 2.0));
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                    await _client!.ChangeMessageVisibilityAsync(
                        _queueUrl, receiptHandle, _options.VisibilityTimeout, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger?.LogDebug(ex, "SQS visibility heartbeat ended for {Queue}", _endpoint.QueueName);
            }
        }, cts.Token);

        return new HeartbeatHandle(cts);
    }

    private sealed class HeartbeatHandle(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static void RegisterTransactedAction(IExchange exchange, string key, ITransactedAction action)
    {
        if (exchange.Properties.TryGetValue(TransactedProcessor.TransactActionPropertyKey, out var raw)
            && raw is ConcurrentDictionary<string, ITransactedAction> existing)
        {
            existing[key] = action;
            return;
        }

        var bag = new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = action,
        };
        exchange.Properties[TransactedProcessor.TransactActionPropertyKey] = bag;
    }

    private static List<string> SplitCsv(string csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
