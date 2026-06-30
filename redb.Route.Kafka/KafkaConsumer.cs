using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Kafka;

/// <summary>
/// Kafka consumer. Polls messages from a Kafka topic and delegates to the processor pipeline.
/// Supports single-message and batch modes, with deferred-commit <see cref="ITransactedAction"/>.
/// </summary>
public sealed class KafkaConsumer : DrainableConsumer
{
    private readonly KafkaEndpoint _endpoint;
    private readonly KafkaEndpointOptions _options;
    private IConsumer<string, byte[]>? _consumer;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => $"kafka:{_endpoint.TopicName}";

    /// <summary>Number of messages successfully processed.</summary>
    public long ProcessedCount { get; private set; }

    /// <summary>Creates a Kafka consumer.</summary>
    public KafkaConsumer(KafkaEndpoint endpoint, IProcessor processor, KafkaEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override Task OnStarting(CancellationToken ct)
    {
        var config = _options.BuildConsumerConfig(_endpoint.ResolvedFactory);

        _consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetValueDeserializer(Deserializers.ByteArray)
            .SetErrorHandler((_, e) =>
            {
                if (e.IsFatal)
                    Logger?.LogError("Kafka consumer fatal error: {Reason} (Code: {Code})", e.Reason, e.Code);
                else
                    Logger?.LogWarning("Kafka consumer event: {Reason} (Code: {Code})", e.Reason, e.Code);
            })
            .SetPartitionsAssignedHandler((c, partitions) =>
            {
                Logger?.LogInformation("Kafka rebalance: assigned {Count} partitions: [{Partitions}]",
                    partitions.Count,
                    string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition}]")));
            })
            .SetPartitionsRevokedHandler((c, partitions) =>
            {
                Logger?.LogInformation("Kafka rebalance: revoked {Count} partitions: [{Partitions}]",
                    partitions.Count,
                    string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition}]")));
                try { c.Commit(partitions); }
                catch (KafkaException) { /* no offset to commit — ok */ }
            })
            .SetPartitionsLostHandler((_, partitions) =>
            {
                Logger?.LogWarning("Kafka rebalance: lost {Count} partitions: [{Partitions}] (involuntary)",
                    partitions.Count,
                    string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition}]")));
            })
            .Build();

        try
        {
            // Subscribe or Assign
            if (_options.PartitionNumber.HasValue)
            {
                var tp = new TopicPartition(_endpoint.TopicName, new Partition(_options.PartitionNumber.Value));
                _consumer.Assign(new[] { tp });
            }
            else
            {
                _consumer.Subscribe(_endpoint.TopicName);
            }

            // Seek
            HandleSeekTo();

            // Log topic/partition metadata (leaders, replicas, ISR)
            LogTopicMetadata();
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Kafka consumer OnStarting failed for topic {Topic}", _endpoint.TopicName);
            _consumer?.Dispose();
            _consumer = null;
            throw;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        return Task.Factory.StartNew(
            () => PollLoop(pollCt, processingCt),
            pollCt,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <inheritdoc />
    protected override Task OnStopped()
    {
        _consumer?.Close();
        _consumer?.Dispose();
        _consumer = null;
        return Task.CompletedTask;
    }

    // ── Poll loop ──

    private async Task PollLoop(CancellationToken pollCt, CancellationToken processingCt)
    {
        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                if (_options.MaxPollRecords > 0)
                    await ProcessBatch(pollCt, processingCt).ConfigureAwait(false);
                else
                    await ProcessSingleMessage(pollCt, processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in Kafka poll loop for topic {Topic}", _endpoint.TopicName);
                try { await Task.Delay(1000, pollCt).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessSingleMessage(CancellationToken pollCt, CancellationToken processingCt)
    {
        var result = _consumer!.Consume(pollCt);
        if (result?.Message is null) return;

        using var activity = StartConsumerActivity(result);

        var exchange = CreateExchange(result);
        IncrementInflight();
        try
        {
            var commitAction = new KafkaCommitAction(_consumer, result, Logger);
            RegisterTransactedAction(exchange, $"kafka-commit-{result.Offset.Value}", commitAction);

            await Processor.Process(exchange, processingCt).ConfigureAwait(false);
            ProcessedCount++;

            // Auto-commit: settle the offset inline after successful processing — UNLESS a
            // transactional route already committed it (commitAction.Committed), in which case
            // the transaction owns the commit and EnableAutoCommit is ignored.
            if (_options.EnableAutoCommit && !commitAction.Committed)
                await commitAction.Commit(processingCt).ConfigureAwait(false);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
            DecrementInflight();
        }
    }

    private async Task ProcessBatch(CancellationToken pollCt, CancellationToken processingCt)
    {
        var batch = new List<ConsumeResult<string, byte[]>>();
        var deadline = DateTime.UtcNow.AddMilliseconds(_options.PollTimeoutMs);

        while (batch.Count < _options.MaxPollRecords && DateTime.UtcNow < deadline && !pollCt.IsCancellationRequested)
        {
            try
            {
                var result = _consumer!.Consume(TimeSpan.FromMilliseconds(100));
                if (result?.Message is not null)
                    batch.Add(result);
            }
            catch (ConsumeException ex)
            {
                Logger?.LogError(ex, "Kafka batch consume error: {Reason}", ex.Error.Reason);
                if (ex.Error.IsFatal) throw;
                break;
            }
        }

        if (batch.Count == 0) return;

        using var activity = StartConsumerActivity(batch[0]);
        activity?.SetTag("messaging.batch.message_count", batch.Count);

        var exchange = CreateBatchExchange(batch);
        IncrementInflight();
        try
        {
            // Commit only the last offset
            var last = batch[^1];
            var commitAction = new KafkaCommitAction(_consumer!, last, Logger);
            RegisterTransactedAction(exchange, $"kafka-batch-commit-{last.Offset.Value}", commitAction);

            await Processor.Process(exchange, processingCt).ConfigureAwait(false);
            ProcessedCount += batch.Count;

            // Auto-commit the last batch offset inline after success — unless a transactional
            // route already committed it (see ProcessSingleMessage for the rationale).
            if (_options.EnableAutoCommit && !commitAction.Committed)
                await commitAction.Commit(processingCt).ConfigureAwait(false);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
            DecrementInflight();
        }
    }

    // ── Trace context propagation ──

    /// <summary>
    /// Extracts W3C trace context from Kafka message headers and starts a Consumer activity
    /// linked to the producer's trace.
    /// </summary>
    private Activity? StartConsumerActivity(ConsumeResult<string, byte[]> result)
    {
        var propagator = DistributedContextPropagator.Current;

        propagator.ExtractTraceIdAndState(result.Message.Headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not Headers h) return;
                try
                {
                    var header = h.FirstOrDefault(x =>
                        string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (header is not null)
                        value = Encoding.UTF8.GetString(header.GetValueBytes());
                }
                catch { /* malformed trace header — skip */ }
            },
            out var traceParent,
            out var traceState);

        ActivityContext parentContext = default;
        if (!string.IsNullOrEmpty(traceParent))
            ActivityContext.TryParse(traceParent, traceState, out parentContext);

        var activity = RouteActivitySource.Source.StartActivity(
            $"{result.Topic} receive",
            ActivityKind.Consumer,
            parentContext);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "kafka");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", result.Topic);
            activity.SetTag("messaging.kafka.consumer.group", _options.GroupId);
            activity.SetTag("messaging.kafka.destination.partition", result.Partition.Value);
            activity.SetTag("messaging.kafka.message.offset", result.Offset.Value);

            if (!string.IsNullOrEmpty(result.Message.Key))
                activity.SetTag("messaging.kafka.message.key", result.Message.Key);
        }

        return activity;
    }

    // ── Exchange creation ──

    private Exchange CreateExchange(ConsumeResult<string, byte[]> result)
    {
        var message = new Message
        {
            Body = result.Message.Value
        };

        // Transfer Kafka headers
        if (result.Message.Headers is not null)
        {
            foreach (var header in result.Message.Headers)
            {
                try
                {
                    var headerValue = Encoding.UTF8.GetString(header.GetValueBytes());
                    message.Headers[header.Key] = headerValue;

                    // Restore ContentType from Kafka header if present
                    if (string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                        message.ContentType = headerValue;
                }
                catch (Exception ex) { Logger?.LogDebug(ex, "Kafka: malformed header '{Key}'", header.Key); }
            }
        }

        // Kafka metadata headers
        message.Headers[KafkaHeaders.Topic] = result.Topic;
        message.Headers[KafkaHeaders.Partition] = result.Partition.Value;
        message.Headers[KafkaHeaders.Offset] = result.Offset.Value;
        if (result.Message.Timestamp.Type != TimestampType.NotAvailable)
            message.Headers[KafkaHeaders.Timestamp] = result.Message.Timestamp.UtcDateTime;
        if (!string.IsNullOrEmpty(result.Message.Key))
            message.Headers[KafkaHeaders.Key] = result.Message.Key;

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOnly;
        return exchange;
    }

    private Exchange CreateBatchExchange(List<ConsumeResult<string, byte[]>> batch)
    {
        var messages = new List<IMessage>(batch.Count);
        foreach (var result in batch)
        {
            var msg = new Message { Body = result.Message.Value };
            msg.Headers[KafkaHeaders.Topic] = result.Topic;
            msg.Headers[KafkaHeaders.Partition] = result.Partition.Value;
            msg.Headers[KafkaHeaders.Offset] = result.Offset.Value;

            if (result.Message.Headers is not null)
            {
                foreach (var h in result.Message.Headers)
                {
                    try { msg.Headers[h.Key] = Encoding.UTF8.GetString(h.GetValueBytes()); }
                    catch (Exception ex) { Logger?.LogDebug(ex, "Kafka: malformed batch header '{Key}'", h.Key); }
                }
            }

            messages.Add(msg);
        }

        var batchMessage = new Message { Body = messages };
        batchMessage.Headers[KafkaHeaders.BatchSize] = batch.Count;

        var exchange = Exchange.Create(batchMessage, _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOnly;
        return exchange;
    }

    // ── Transacted action registration ──

    private static void RegisterTransactedAction(IExchange exchange, string key, ITransactedAction action)
    {
        if (!exchange.Properties.TryGetValue("TRANSACT_ACTION", out var raw) ||
            raw is not ConcurrentDictionary<string, ITransactedAction> dict)
        {
            dict = new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase);
            exchange.Properties["TRANSACT_ACTION"] = dict;
        }

        dict[key] = action;
    }

    // ── Seek ──

    private void HandleSeekTo()
    {
        if (string.IsNullOrWhiteSpace(_options.SeekTo)) return;

        var assignment = _consumer!.Assignment;
        if (assignment is null || assignment.Count == 0) return;

        var offset = _options.SeekTo.Trim().ToLowerInvariant() switch
        {
            "beginning" => Offset.Beginning,
            "end" => Offset.End,
            _ => (Offset?)null
        };

        if (offset.HasValue)
        {
            _consumer.Assign(assignment.Select(tp => new TopicPartitionOffset(tp, offset.Value)));
            Logger?.LogInformation("Kafka consumer: seek to {SeekTo} for topic {Topic}", _options.SeekTo, _endpoint.TopicName);
        }
    }

    private void LogTopicMetadata()
    {
        try
        {
            using var adminClient = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = _options.Brokers }).Build();
            var metadata = adminClient.GetMetadata(_endpoint.TopicName, TimeSpan.FromSeconds(5));
            var topicMeta = metadata.Topics.FirstOrDefault(t => t.Topic == _endpoint.TopicName);
            if (topicMeta is null) return;

            Logger?.LogInformation("Kafka topic {Topic}: {PartitionCount} partitions, {BrokerCount} brokers",
                _endpoint.TopicName, topicMeta.Partitions.Count, metadata.Brokers.Count);

            foreach (var p in topicMeta.Partitions)
            {
                var leader = metadata.Brokers.FirstOrDefault(b => b.BrokerId == p.Leader);
                Logger?.LogInformation(
                    "  Partition [{PartitionId}]: leader=broker#{LeaderId} ({Host}:{Port}), replicas=[{Replicas}], ISR=[{ISR}]",
                    p.PartitionId, p.Leader,
                    leader?.Host ?? "?", leader?.Port ?? 0,
                    string.Join(", ", p.Replicas),
                    string.Join(", ", p.InSyncReplicas));
            }
        }
        catch (Exception ex)
        {
            Logger?.LogDebug(ex, "Kafka: could not fetch topic metadata for {Topic}", _endpoint.TopicName);
        }
    }
}

/// <summary>
/// Deferred Kafka offset commit action. Commits or rolls back the offset for an exchange.
/// </summary>
internal sealed class KafkaCommitAction : ITransactedAction
{
    private readonly IConsumer<string, byte[]> _consumer;
    private readonly ConsumeResult<string, byte[]> _result;
    private readonly ILogger? _logger;
    private int _committed;

    public KafkaCommitAction(IConsumer<string, byte[]> consumer, ConsumeResult<string, byte[]> result, ILogger? logger)
    {
        _consumer = consumer;
        _result = result;
        _logger = logger;
    }

    /// <summary>True once the offset has been committed (by a transactional route or inline auto-commit).</summary>
    public bool Committed => Volatile.Read(ref _committed) == 1;

    public Task Commit(CancellationToken ct = default)
    {
        // Idempotent: the transactional route and the inline auto-commit path may both reach here;
        // only the first wins. (Kafka offset commit is itself idempotent, but the flag also lets the
        // consumer skip the redundant inline call after a transactional commit.)
        if (Interlocked.Exchange(ref _committed, 1) != 0)
            return Task.CompletedTask;

        _consumer.Commit(_result);
        _logger?.LogDebug("Kafka offset committed: topic={Topic}, partition={Partition}, offset={Offset}",
            _result.Topic, _result.Partition.Value, _result.Offset.Value);
        return Task.CompletedTask;
    }

    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("Kafka offset rollback (no commit): topic={Topic}, partition={Partition}, offset={Offset}",
            _result.Topic, _result.Partition.Value, _result.Offset.Value);
        return Task.CompletedTask;
    }
}
