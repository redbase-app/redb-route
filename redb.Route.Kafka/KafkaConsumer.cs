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

    private long _processedCount;

    /// <summary>Number of messages successfully processed. Thread-safe (волна A7).</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

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
        var config = _options.BuildConsumerConfig(_endpoint.ResolvedFactory, _endpoint.Uri.RawParameters);

        _consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetValueDeserializer(Deserializers.ByteArray)
            .SetErrorHandler((_, e) =>
            {
                // Log-only: a fatal error also surfaces as a ConsumeException on the poll loop's
                // next Consume, and the loop records it there - recording here too made one
                // fatal event count 2-3 times (ревью дуги, M2).
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

                // Волна A4: seekTo rides the assignment, because right after Subscribe() the
                // assignment is empty - the old post-Subscribe seek was a silent no-op for every
                // group consumer.
                // Ревью дуги (M1): "once" is per PARTITION, not per callback. The first callback
                // can be empty (more consumers than partitions), and CooperativeSticky delivers
                // partitions incrementally across several callbacks - a whole-consumer one-shot
                // flag either burned on an empty list or seeked only the first increment. Each
                // partition seeks exactly once, on ITS first assignment to this consumer; a
                // partition re-assigned by a later rebalance must not replay.
                var mapped = partitions
                    .Select(p => new TopicPartitionOffset(p, SeekOffsetFor(p) ?? Offset.Unset))
                    .ToList();

                var seeked = mapped.Where(m => m.Offset != Offset.Unset).ToList();
                if (seeked.Count > 0)
                    Logger?.LogInformation(
                        "Kafka consumer: seek to {SeekTo} on first assignment of [{Partitions}]",
                        _options.SeekTo,
                        string.Join(", ", seeked.Select(m => $"{m.Topic}[{m.Partition}]")));
                return mapped;
            })
            .SetPartitionsRevokedHandler((c, partitions) =>
            {
                Logger?.LogInformation("Kafka rebalance: revoked {Count} partitions: [{Partitions}]",
                    partitions.Count,
                    string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition}]")));
                // Ревью дуги (H1): NO commit here. Commit(partitions) settles the consumer's
                // POSITION, which advances on Consume - not on processing. Rebalance callbacks
                // run inside Consume, so during batch collection this handler used to commit
                // records that were consumed but never processed: the new owner started past
                // them and the record was lost, contradicting the "will be redelivered" log.
                // Every processed record is already settled inline (single: per message; batch:
                // per partition at batch end), and no Consume happens between processing and
                // that settle - so there is nothing legitimate left to commit on revoke.
                // Anything consumed-but-unprocessed correctly replays: at-least-once.
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
                // The assigned handler does not fire for a manual Assign, so the seek offset goes
                // directly into the assignment here.
                var seek = SeekOffsetFor(tp);
                if (seek is null)
                    _consumer.Assign(new[] { tp });
                else
                    _consumer.Assign(new[] { new TopicPartitionOffset(tp, seek.Value) });
            }
            else
            {
                // Волна A2: librdkafka treats only a "^"-prefixed name as a regex. The flag used
                // to be read by nothing, so the only working form was writing the "^" by hand.
                var topic = _options.TopicIsPattern && !_endpoint.TopicName.StartsWith('^')
                    ? "^" + _endpoint.TopicName
                    : _endpoint.TopicName;
                _consumer.Subscribe(topic);
            }

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
                // Transport-level failure (consume error, broker gone) - invisible to the core's
                // statistics wrappers, so the endpoint records it itself (волна A5).
                _endpoint.RecordError(ex);

                // A fatal librdkafka error is unrecoverable for this client instance - retrying
                // it forever at one log line per second is noise, not resilience (волна A7).
                if (ex is KafkaException { Error.IsFatal: true })
                {
                    Logger?.LogCritical(ex,
                        "Kafka consumer stopping: fatal client error on topic {Topic}. " +
                        "The endpoint's error statistics carry the failure; restart the route to recover",
                        _endpoint.TopicName);
                    break;
                }

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

            try
            {
                await Processor.Process(exchange, processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (processingCt.IsCancellationRequested)
            {
                throw; // shutdown, not a processing failure
            }
            catch (Exception ex)
            {
                // Волна A2: an unhandled processing failure used to escape to the poll loop, and
                // the NEXT successful commit then covered this record's offset - a Kafka commit is
                // a position, not a per-record mark - so the message was effectively lost with
                // nothing but a log line.
                await HandleProcessingFailure(ex, [result], pollCt).ConfigureAwait(false);
                return;
            }

            Interlocked.Increment(ref _processedCount);

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

    /// <summary>
    /// One place for the two failure policies. <c>breakOnFirstError=true</c> (Camel semantics):
    /// seek every partition of the failed delivery back to its first offset, so the same
    /// record(s) come again — the route retries instead of losing the message; the poll loop
    /// then waits a beat so a permanently poisoned record does not become a hot spin. Default
    /// <c>false</c> (also Camel's default): move on, but explicitly — the error lands in
    /// endpoint statistics, and the log says the offset will be covered by the next commit.
    /// </summary>
    private async Task HandleProcessingFailure(Exception ex, IReadOnlyList<ConsumeResult<string, byte[]>> failed, CancellationToken pollCt)
    {
        // Deliberately NOT RecordError here: in a routed consumer the processor chain is wrapped
        // in the core's StatisticsProcessor, which has already recorded this escaping exception
        // against the same endpoint - recording again would double-count (принцип 0, волна A5).
        // The connector records only what the core cannot see: transport-level poll errors.
        if (_options.BreakOnFirstError)
        {
            foreach (var group in failed.GroupBy(r => r.TopicPartition))
            {
                var first = new TopicPartitionOffset(group.Key, group.Min(r => r.Offset.Value));
                try
                {
                    _consumer!.Seek(first);
                }
                catch (KafkaException seekEx)
                {
                    // The partition may have been revoked between consume and seek. The offset is
                    // not committed, so the record is redelivered to whoever owns the partition now.
                    Logger?.LogWarning(seekEx,
                        "Kafka breakOnFirstError: seek back to {Tpo} failed (partition revoked?); " +
                        "the uncommitted record will be redelivered by the group", first);
                }
            }

            Logger?.LogError(ex,
                "Kafka message processing failed: topic={Topic}; breakOnFirstError=true - seeking back and retrying",
                _endpoint.TopicName);

            // Without a pause a permanently failing record retries in a tight loop.
            try { await Task.Delay(1000, pollCt).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return;
        }

        Logger?.LogError(ex,
            "Kafka message processing failed: topic={Topic}; breakOnFirstError=false - moving on. " +
            "The failed record's offset will be covered by the next successful commit " +
            "(use a route error handler or breakOnFirstError=true to keep it)",
            _endpoint.TopicName);
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
                // Fatal rethrows into the poll loop, which records it once; recording here too
                // double-counted the same event (ревью дуги, M2).
                if (ex.Error.IsFatal) throw;
                _endpoint.RecordError(ex);
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
            // Settle the last offset of EVERY partition the batch touched (волна A6.4)
            var last = batch[^1];
            var commitAction = new KafkaCommitAction(_consumer!, batch, Logger);
            RegisterTransactedAction(exchange, $"kafka-batch-commit-{last.Offset.Value}", commitAction);

            try
            {
                await Processor.Process(exchange, processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (processingCt.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Волна A2: the batch failed as one unit - with breakOnFirstError every partition
                // of the batch is sought back to its first offset, so the whole batch comes again.
                await HandleProcessingFailure(ex, batch, pollCt).ConfigureAwait(false);
                return;
            }

            Interlocked.Add(ref _processedCount, batch.Count);

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

        Activity? activity;
        if (parentContext == default)
        {
            // Волна A7: StartActivity(parentContext: default) does NOT create a root span - it
            // inherits Activity.Current, so an ambient activity left by the host would adopt
            // every receive. Clear it first (the house pattern, see DetachedDispatch.Enter).
            var savedCurrent = Activity.Current;
            Activity.Current = null;
            activity = RouteActivitySource.Source.StartActivity(
                $"{result.Topic} receive", ActivityKind.Consumer);
            if (activity is null)
                Activity.Current = savedCurrent;
        }
        else
        {
            activity = RouteActivitySource.Source.StartActivity(
                $"{result.Topic} receive", ActivityKind.Consumer, parentContext);
        }

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
                    try
                    {
                        var headerValue = Encoding.UTF8.GetString(h.GetValueBytes());
                        msg.Headers[h.Key] = headerValue;

                        // Волна A7: the single-message path restores ContentType, the batch path
                        // did not - one topic gave a different message shape depending on
                        // maxPollRecords.
                        if (string.Equals(h.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                            msg.ContentType = headerValue;
                    }
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

    private readonly HashSet<TopicPartition> _seekedPartitions = [];

    /// <summary>
    /// The seekTo position, handed out exactly once: the first assignment (or the manual Assign)
    /// gets it, every later rebalance resumes from committed offsets like any group member.
    /// </summary>
    /// <summary>
    /// The seekTo offset for <paramref name="partition"/>, or null once it has already been
    /// seeked. "On first start" is a per-partition promise: each partition seeks exactly once,
    /// on its first assignment to this consumer - never again on a later rebalance.
    /// </summary>
    private Offset? SeekOffsetFor(TopicPartition partition)
    {
        if (string.IsNullOrWhiteSpace(_options.SeekTo)) return null;
        lock (_seekedPartitions)
        {
            if (!_seekedPartitions.Add(partition)) return null;
        }
        return KafkaOptionParsers.ParseSeekTo(_options.SeekTo);
    }

    private void LogTopicMetadata()
    {
        // Purely diagnostic - skip the round-trip entirely unless someone will see it.
        if (Logger?.IsEnabled(LogLevel.Information) != true) return;

        try
        {
            // Волна A6.2: the admin client carries the SAME security as the consumer. It used to
            // be built from bare brokers, so on a SASL/SSL cluster every consumer start paid a 5s
            // timeout for a log line that never appeared - while the factory's ready
            // BuildAdminConfig() sat unused (принцип 0 плана).
            var adminConfig = _endpoint.ResolvedFactory?.BuildAdminConfig()
                ?? _options.BuildAdminConfig();
            if (!string.IsNullOrWhiteSpace(_options.Brokers))
                adminConfig.BootstrapServers = _options.Brokers;

            using var adminClient = new AdminClientBuilder(adminConfig).Build();
            var metadata = adminClient.GetMetadata(_endpoint.TopicName, TimeSpan.FromSeconds(5));
            var topicMeta = metadata.Topics.FirstOrDefault(t => t.Topic == _endpoint.TopicName);
            if (topicMeta is null) return;

            // One line per consumer start; per-partition leader/ISR detail is Debug - a
            // 100-partition topic used to print a hundred Information lines per start.
            Logger?.LogInformation("Kafka topic {Topic}: {PartitionCount} partitions, {BrokerCount} brokers",
                _endpoint.TopicName, topicMeta.Partitions.Count, metadata.Brokers.Count);

            if (Logger?.IsEnabled(LogLevel.Debug) == true)
            {
                foreach (var p in topicMeta.Partitions)
                {
                    var leader = metadata.Brokers.FirstOrDefault(b => b.BrokerId == p.Leader);
                    Logger?.LogDebug(
                        "  Partition [{PartitionId}]: leader=broker#{LeaderId} ({Host}:{Port}), replicas=[{Replicas}], ISR=[{ISR}]",
                        p.PartitionId, p.Leader,
                        leader?.Host ?? "?", leader?.Port ?? 0,
                        string.Join(", ", p.Replicas),
                        string.Join(", ", p.InSyncReplicas));
                }
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
    private readonly IReadOnlyList<TopicPartitionOffset>? _batchOffsets;
    private readonly ILogger? _logger;
    private int _committed;

    public KafkaCommitAction(IConsumer<string, byte[]> consumer, ConsumeResult<string, byte[]> result, ILogger? logger)
    {
        _consumer = consumer;
        _result = result;
        _logger = logger;
    }

    /// <summary>
    /// Batch settle (волна A6.4): the position of EVERY partition the batch touched. The single
    /// "commit the last result" used to settle one partition only, leaving the rest to the
    /// revoked-handler at clean shutdown — a kill -9 replayed far more than needed.
    /// A committed position is the NEXT offset to read, hence the +1.
    /// </summary>
    public KafkaCommitAction(IConsumer<string, byte[]> consumer,
        IReadOnlyList<ConsumeResult<string, byte[]>> batch, ILogger? logger)
    {
        _consumer = consumer;
        _result = batch[^1];
        _batchOffsets = batch
            .GroupBy(r => r.TopicPartition)
            .Select(g => new TopicPartitionOffset(g.Key, g.Max(r => r.Offset.Value) + 1))
            .ToList();
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

        if (_batchOffsets is not null)
        {
            _consumer.Commit(_batchOffsets);
            _logger?.LogDebug("Kafka batch offsets committed: [{Offsets}]",
                string.Join(", ", _batchOffsets.Select(o => $"{o.Topic}[{o.Partition.Value}]@{o.Offset.Value}")));
        }
        else
        {
            _consumer.Commit(_result);
            _logger?.LogDebug("Kafka offset committed: topic={Topic}, partition={Partition}, offset={Offset}",
                _result.Topic, _result.Partition.Value, _result.Offset.Value);
        }
        return Task.CompletedTask;
    }

    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("Kafka offset rollback (no commit): topic={Topic}, partition={Partition}, offset={Offset}",
            _result.Topic, _result.Partition.Value, _result.Offset.Value);
        return Task.CompletedTask;
    }
}
