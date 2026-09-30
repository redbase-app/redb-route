using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Transactions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Kafka;

/// <summary>
/// Kafka producer. Sends messages to a Kafka topic with support for transactional
/// deferred-commit via <see cref="ITransactedAction"/>, and for Kafka transactions when
/// <see cref="KafkaEndpointOptions.TransactionalIdPrefix"/> is set.
/// </summary>
public sealed class KafkaProducer : ConnectableProducer
{
    /// <summary>
    /// How long InitTransactions and SendOffsetsToTransaction may take before they fail. Commit and abort take no
    /// timeout of ours (see <c>CommitWithRetries</c>).
    /// </summary>
    private static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(30);

    private static int s_transactionalProducers;

    private readonly KafkaEndpoint _endpoint;
    private readonly KafkaEndpointOptions _options;
    private IProducer<string, byte[]>? _producer;

    // Kafka transactions: one open transaction per producer at a time, so the transactions take turns; the id is the
    // producer's own and survives a rebuild after a fatal error (InitTransactions with it fences the dead incarnation
    // and aborts what it left open, instead of leaving that to block read_committed readers until it times out).
    private readonly SemaphoreSlim _transactionTurn = new(1, 1);
    private readonly string _batchKey = $"kafka-tx-{Guid.NewGuid():N}";
    private string? _transactionalId;
    private string _cluster = "";
    private bool _rebuild;
    private int _warnedForeignOffsets;
    private int _warnedClosedOffsets;

    /// <summary>Creates a Kafka producer.</summary>
    public KafkaProducer(KafkaEndpoint endpoint, KafkaEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"kafka:{_endpoint.TopicName}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        var config = _options.BuildProducerConfig(_endpoint.ResolvedFactory, _endpoint.Uri.RawParameters);
        KafkaOptionParsers.WarnIfPasswordInClear(config, Logger, ProducerName);

        if (_options.TransactionalIdPrefix is { } prefix)
        {
            // An id of this producer's own: nodes deploying the same configuration, and two producers of one process,
            // would fence each other with a shared one.
            _transactionalId ??= $"{prefix}-{Environment.MachineName}-{Environment.ProcessId}-" +
                                 Interlocked.Increment(ref s_transactionalProducers);
            config.TransactionalId = _transactionalId;
        }

        _producer = new ProducerBuilder<string, byte[]>(config)
            .SetValueSerializer(Serializers.ByteArray)
            .SetErrorHandler((_, e) =>
                Logger?.LogError("Kafka producer error: {Reason} (Code: {Code})", e.Reason, e.Code))
            .Build();

        if (_transactionalId is not null)
        {
            var producer = _producer;
            await Task.Run(() => producer.InitTransactions(TransactionTimeout), ct).ConfigureAwait(false);
            // The cluster whose consumed offsets this producer's transactions may take; the broker has just answered.
            _cluster = await KafkaConsumedOffsets.ClusterIdOf(producer.Handle, TransactionTimeout).ConfigureAwait(false);
            Logger?.LogInformation(
                "Kafka producer uses transactions: topic={Topic}, transactional.id={TransactionalId}, cluster={Cluster}",
                _endpoint.TopicName, _transactionalId, _cluster);
        }
    }

    /// <inheritdoc />
    protected override Task DisconnectAsync(CancellationToken ct)
    {
        if (_producer is not null)
        {
            Logger?.LogInformation("Kafka producer flushing: topic={Topic}", _endpoint.TopicName);
            _producer.Flush(TimeSpan.FromSeconds(30));
            _producer.Dispose();
            _producer = null;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            (_endpoint.Component as ComponentBase)?.Context,
            $"{_endpoint.TopicName} publish", ActivityKind.Producer,
            "messaging.system", "kafka", _endpoint.Uri.NormalizedKey,
            destination: _endpoint.TopicName, operation: "publish");

        var message = PrepareMessage(exchange);

        // The context of this send, over a traceparent copied from a consumed record's headers: that one names the
        // previous hop.
        RouteTelemetryExtensions.InjectTraceContext(activity, message.Headers, WriteTraceHeader);

        try
        {
            await SendAsync(exchange, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only a cancellation by the caller's own token is not a failure; any other (a client timeout) is.
            // A send deferred to the block's commit fails later, outside this span; the route's span shows that one.
            activity.RecordFailure(ex);
            throw;
        }
    }

    /// <summary>
    /// Writes a trace field to Kafka headers as UTF-8, replacing every value already there under that name in any case:
    /// the consumer reads the name without case and takes the first, so a <c>Traceparent</c> bridged from an HTTP caller
    /// would otherwise win over this send's <c>traceparent</c>. <see cref="Headers.Remove"/> compares the name exactly.
    /// </summary>
    private static readonly TraceHeaderWriter<Headers> WriteTraceHeader = static (headers, name, value) =>
    {
        foreach (var key in headers.Select(h => h.Key)
                     .Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.Ordinal)
                     .ToList())
            headers.Remove(key);
        headers.Add(name, Encoding.UTF8.GetBytes(value));
    };

    private async Task SendAsync(IExchange exchange, Message<string, byte[]> message, CancellationToken ct)
    {
        if (_transactionalId is not null)
        {
            var send = new KafkaTransactionalSend(KafkaSendAction.CloneMessage(message), exchange);
            if (TransactedActions.Defers(exchange, _options.Transacted))
            {
                // The block's sends through this producer commit as one Kafka transaction when the block commits,
                // with the consumed offset when the route started from a Kafka consumer of this cluster.
                TransactedActions.JoinBatch(exchange, _batchKey,
                        () => new KafkaTransactionBatch(this, OffsetsOfThisCluster(exchange)),
                        ProducerName)
                    .Add(send);
            }
            else
            {
                // A transactional producer sends nothing outside a transaction: this send is a transaction of its own.
                await CommitTransactionAsync([send], offsets: null, ct).ConfigureAwait(false);
            }
            return;
        }

        if (TransactedActions.Defers(exchange, _options.Transacted))
        {
            // Deferred transactional send — actual publish happens on commit
            var action = new KafkaSendAction(_producer, _endpoint, message,
                _options.PartitionNumber, _options.RecordMetadata, exchange, Logger);
            TransactedActions.Register(exchange, $"kafka-send-{Guid.NewGuid():N}", action, ProducerName);
        }
        else
        {
            // Immediate send
            DeliveryResult<string, byte[]> result;

            try
            {
                if (_options.PartitionNumber.HasValue)
                {
                    var tp = new TopicPartition(_endpoint.TopicName, new Partition(_options.PartitionNumber.Value));
                    result = await _producer.ProduceAsync(tp, message, ct).ConfigureAwait(false);
                }
                else
                {
                    result = await _producer.ProduceAsync(_endpoint.TopicName, message, ct).ConfigureAwait(false);
                }
            }
            catch (ProduceException<string, byte[]> ex)
            {
                Logger?.LogError(ex, "Kafka produce failed: topic={Topic}, partition={Partition}, error={Error}",
                    _endpoint.TopicName, _options.PartitionNumber, ex.Error.Reason);
                throw;
            }

            Logger?.LogDebug("Kafka message sent: topic={Topic}, partition={Partition}, offset={Offset}",
                result.Topic, result.Partition.Value, result.Offset.Value);

            // Wire-level bytes are the connector's to record: the core's ToProcessor counts
            // MessagesOut/Errors for a routed producer but has no idea of payload sizes (волна A5).
            _endpoint.RecordBytesOut(message.Value.Length);

            if (_options.RecordMetadata)
                AddDeliveryMetadata(exchange, result);
        }
    }

    // ── Kafka transactions ──

    /// <summary>
    /// Commits <paramref name="sends"/> as one Kafka transaction: begin, produce each in order, add the consumed offsets
    /// when given and still claimable, commit. Any failure aborts the transaction — nothing becomes visible to
    /// read_committed readers, the offsets stay uncommitted — and propagates. The producer's transactions take turns.
    /// </summary>
    internal async Task CommitTransactionAsync(
        IReadOnlyList<KafkaTransactionalSend> sends, KafkaConsumedOffsets? offsets, CancellationToken ct)
    {
        await _transactionTurn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var producer = await TransactionalProducerAsync(ct).ConfigureAwait(false);
            var claimed = offsets?.TryClaim() == true;
            if (offsets is { IsClosed: true })
                WarnOnce(ref _warnedClosedOffsets,
                    "Kafka transaction on topic {Topic} commits after the route returned to its Kafka consumer (an " +
                    "asynchronous step inside the .Transacted() block?): the consumed offsets are not in the transaction, " +
                    "the consumer settled them itself, and the route is not exactly-once. Keep the block synchronous.",
                    _endpoint.TopicName);
            var delivered = new DeliveryResult<string, byte[]>[sends.Count];

            try
            {
                producer.BeginTransaction();
                for (var i = 0; i < sends.Count; i++)
                    delivered[i] = await ProduceAsync(producer, sends[i].Message, ct).ConfigureAwait(false);
                if (claimed)
                    producer.SendOffsetsToTransaction(offsets!.Offsets, offsets.GroupMetadata, TransactionTimeout);
            }
            catch (Exception ex)
            {
                if (claimed)
                    offsets!.Release();
                Abort(producer, ex);
                throw;
            }

            try
            {
                CommitWithRetries(producer);
            }
            catch (KafkaRetriableException ex)
            {
                // The commit may have reached the coordinator and succeeded: aborting now is not the transaction's
                // undo, only a call librdkafka refuses. The next incarnation's InitTransactions settles it (the
                // coordinator completes a prepared commit, aborts an open transaction).
                if (claimed)
                    offsets!.Release();
                _rebuild = true;
                Logger?.LogError(ex,
                    "Kafka transaction of producer {TransactionalId} on topic {Topic}: the commit did not answer, so its " +
                    "outcome is unknown. The producer is rebuilt before the next transaction; if this one did commit, a " +
                    "redelivery of the exchange writes its records again.", _transactionalId, _endpoint.TopicName);
                throw;
            }
            catch (Exception ex)
            {
                if (claimed)
                    offsets!.Release();
                Abort(producer, ex);
                throw;
            }

            if (claimed)
                offsets!.MarkCommitted();
            for (var i = 0; i < sends.Count; i++)
            {
                _endpoint.RecordBytesOut(sends[i].Message.Value.Length);
                if (_options.RecordMetadata)
                    AddDeliveryMetadata(sends[i].Exchange, delivered[i]);
            }
            Logger?.LogDebug("Kafka transaction committed: topic={Topic}, messages={Count}, consumed offsets={Offsets}",
                _endpoint.TopicName, sends.Count, claimed);
        }
        finally
        {
            _transactionTurn.Release();
        }
    }

    /// <summary>
    /// The offsets the route's Kafka consumer offered, when it reads the cluster this producer writes to: only that
    /// cluster's transaction can commit them. Clusters are compared by the id their brokers report, however the brokers
    /// are listed on the two endpoints. Another cluster (or one the consumer could not identify) leaves the offsets to the
    /// consumer, and the route is at-least-once; that is said out loud.
    /// </summary>
    private KafkaConsumedOffsets? OffsetsOfThisCluster(IExchange exchange)
    {
        var offered = KafkaConsumedOffsets.OfferedOn(exchange);
        if (offered is null || offered.Cluster == _cluster)
            return offered;

        WarnOnce(ref _warnedForeignOffsets,
            "Kafka transaction on topic {Topic} cannot take the consumed offsets: the route's consumer reads cluster " +
            "'{ConsumerCluster}', this producer writes to cluster '{ProducerCluster}'. The consumer commits them itself, " +
            "and the route is at-least-once, not exactly-once.",
            _endpoint.TopicName, offered.Cluster ?? "(not identified)", _cluster);
        return null;
    }

    /// <summary>
    /// Warns the first time a producer meets a condition that holds for every exchange of its route (its configuration or
    /// shape), then at Debug: a warning per message would bury the log without saying more.
    /// </summary>
    private void WarnOnce(ref int warned, string message, params object?[] args)
    {
        var level = Interlocked.Exchange(ref warned, 1) == 0 ? LogLevel.Warning : LogLevel.Debug;
        Logger?.Log(level, message, args);
    }

    private async Task<IProducer<string, byte[]>> TransactionalProducerAsync(CancellationToken ct)
    {
        if (_rebuild)
        {
            // The previous incarnation hit a fatal error (fenced, say) and can do nothing more. A new one with the same
            // transactional.id fences it for good and aborts whatever it left open.
            Logger?.LogWarning("Kafka producer {TransactionalId} is rebuilt after a fatal error", _transactionalId);
            _producer?.Dispose();
            _producer = null;
            await ConnectAsync(ct).ConfigureAwait(false);
            _rebuild = false;

            // The rebuild goes round Start: a Stop that ran meanwhile found no client to close, and the new one would
            // outlive the producer. Transactions never race a rebuild (they take turns); Stop does.
            if (!IsStarted)
            {
                Interlocked.Exchange(ref _producer, null)?.Dispose();
                throw new InvalidOperationException(
                    $"Kafka producer for topic '{_endpoint.TopicName}' was stopped while it was rebuilt: the transaction " +
                    "cannot be committed.");
            }
        }
        return _producer ?? throw new InvalidOperationException(
            $"Kafka producer for topic '{_endpoint.TopicName}' is stopped: the transaction cannot be committed.");
    }

    private Task<DeliveryResult<string, byte[]>> ProduceAsync(
        IProducer<string, byte[]> producer, Message<string, byte[]> message, CancellationToken ct) =>
        _options.PartitionNumber is { } partition
            ? producer.ProduceAsync(new TopicPartition(_endpoint.TopicName, new Partition(partition)), message, ct)
            : producer.ProduceAsync(_endpoint.TopicName, message, ct);

    private void CommitWithRetries(IProducer<string, byte[]> producer)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // No timeout of ours: librdkafka strongly recommends it (its API timeouts do not match the protocol
                // requests); the call blocks up to transaction.timeout.ms instead.
                producer.CommitTransaction();
                return;
            }
            catch (KafkaRetriableException ex) when (attempt < 3)
            {
                // A retriable commit failure (a timeout, say) leaves the transaction open: the commit may be called
                // again, and it resumes the same commit.
                Logger?.LogWarning(ex, "Kafka transaction commit failed with a retriable error; retrying (attempt {Attempt})",
                    attempt);
            }
        }
    }

    private void Abort(IProducer<string, byte[]> producer, Exception failure)
    {
        if (failure is KafkaException { Error.IsFatal: true })
        {
            _rebuild = true;
            Logger?.LogError(failure,
                "Kafka producer {TransactionalId} hit a fatal error; its transaction is lost and it is rebuilt before the next one",
                _transactionalId);
            return;
        }

        try
        {
            producer.AbortTransaction(); // no timeout of ours, as for the commit
        }
        catch (KafkaException abortFailure)
        {
            // Not committed either way; a producer that cannot abort is not trusted with the next transaction.
            _rebuild = true;
            Logger?.LogError(abortFailure,
                "Kafka producer {TransactionalId} could not abort its transaction; it is rebuilt before the next one",
                _transactionalId);
        }
    }

    // ── Message preparation ──

    private Message<string, byte[]> PrepareMessage(IExchange exchange)
    {
        var body = exchange.In.Body;
        byte[] valueBytes = body switch
        {
            byte[] bytes => bytes,
            string str   => Encoding.UTF8.GetBytes(str),
            null         => Array.Empty<byte>(),
            _            => Encoding.UTF8.GetBytes(body.ToString() ?? string.Empty)
        };

        var msg = new Message<string, byte[]>
        {
            Value = valueBytes,
            Key = DetermineKey(exchange),
            Headers = new Headers()
        };

        // Propagate ContentType as Kafka header if set
        var hasContentType = !string.IsNullOrEmpty(exchange.In.ContentType);
        if (hasContentType)
        {
            msg.Headers.Add("content-type", Encoding.UTF8.GetBytes(exchange.In.ContentType!));
        }

        // Propagate headers (skip kafka.* internal ones)
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (KafkaHeaders.IsRedbHeader(key))
                continue;
            // A consumed record keeps its content-type header next to ContentType: one on the wire, ContentType's.
            if (hasContentType && string.Equals(key, "content-type", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(value?.ToString() ?? string.Empty);
                msg.Headers.Add(key, bytes);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Failed to add header {Key} to Kafka message", key);
            }
        }

        return msg;
    }

    private string? DetermineKey(IExchange exchange)
    {
        // Explicit partition → no key (partitioner is bypassed)
        if (_options.PartitionNumber.HasValue)
            return null;

        // Key from options — supports ${...} expressions (resolved per message)
        if (!string.IsNullOrWhiteSpace(_options.Key))
        {
            var resolved = _options.ResolveOption(_options.Key, exchange);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;
        }

        // The consumed record's key, when the route passes the record on.
        if (_options.KeyFromHeader && exchange.In.Headers.TryGetValue(KafkaHeaders.Key, out var header)
                                   && header?.ToString() is { Length: > 0 } consumedKey)
            return consumedKey;

        return null;
    }

    private static void AddDeliveryMetadata(IExchange exchange, DeliveryResult<string, byte[]> result)
    {
        exchange.In.Headers[KafkaHeaders.SentTopic] = result.Topic;
        exchange.In.Headers[KafkaHeaders.SentPartition] = result.Partition.Value;
        exchange.In.Headers[KafkaHeaders.SentOffset] = result.Offset.Value;
        exchange.In.Headers[KafkaHeaders.SentTimestamp] = DateTimeOffset.UtcNow;
    }

}

/// <summary>
/// Deferred Kafka send action. The message is captured at creation time
/// and published only on <see cref="Commit"/>. On <see cref="Rollback"/> the message is discarded.
/// </summary>
internal sealed class KafkaSendAction : ITransactedAction
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly KafkaEndpoint _endpoint;
    private readonly string _topicName;
    private readonly Message<string, byte[]> _message;
    private readonly int? _partition;
    private readonly bool _recordMetadata;
    private readonly IExchange _exchange;
    private readonly ILogger? _logger;

    public KafkaSendAction(
        IProducer<string, byte[]> producer,
        KafkaEndpoint endpoint,
        Message<string, byte[]> message,
        int? partition,
        bool recordMetadata,
        IExchange exchange,
        ILogger? logger)
    {
        _producer = producer;
        _endpoint = endpoint;
        _topicName = endpoint.TopicName;
        _message = CloneMessage(message);
        _partition = partition;
        _recordMetadata = recordMetadata;
        _exchange = exchange;
        _logger = logger;
    }

    public async Task Commit(CancellationToken ct = default)
    {
        DeliveryResult<string, byte[]> result;

        if (_partition.HasValue)
        {
            var tp = new TopicPartition(_topicName, new Partition(_partition.Value));
            result = await _producer.ProduceAsync(tp, _message, ct).ConfigureAwait(false);
        }
        else
        {
            result = await _producer.ProduceAsync(_topicName, _message, ct).ConfigureAwait(false);
        }

        _logger?.LogDebug("Kafka transactional send committed: topic={Topic}, partition={Partition}, offset={Offset}",
            result.Topic, result.Partition.Value, result.Offset.Value);

        _endpoint.RecordBytesOut(_message.Value.Length);

        if (_recordMetadata)
        {
            _exchange.In.Headers[KafkaHeaders.SentTopic] = result.Topic;
            _exchange.In.Headers[KafkaHeaders.SentPartition] = result.Partition.Value;
            _exchange.In.Headers[KafkaHeaders.SentOffset] = result.Offset.Value;
            _exchange.In.Headers[KafkaHeaders.SentTimestamp] = DateTimeOffset.UtcNow;
        }
    }

    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("Kafka transactional send rolled back (message discarded): topic={Topic}", _topicName);
        return Task.CompletedTask;
    }

    internal static Message<string, byte[]> CloneMessage(Message<string, byte[]> source)
    {
        var clone = new Message<string, byte[]>
        {
            Key = source.Key,
            Value = source.Value,
            Headers = new Headers()
        };

        if (source.Headers is not null)
        {
            foreach (var h in source.Headers)
            {
                var bytes = h.GetValueBytes();
                var copy = new byte[bytes.Length];
                Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
                clone.Headers.Add(h.Key, copy);
            }
        }

        return clone;
    }
}

/// <summary>One send of a Kafka-transactional producer: the message, and the exchange its delivery metadata goes to.</summary>
internal sealed record KafkaTransactionalSend(Message<string, byte[]> Message, IExchange Exchange);

/// <summary>
/// The sends a Kafka-transactional producer deferred in one <c>.Transacted()</c> block, committed as one Kafka transaction
/// when the block commits, together with the offsets the route's Kafka consumer offered (<see cref="KafkaConsumedOffsets"/>).
/// </summary>
internal sealed class KafkaTransactionBatch : ITransactedAction
{
    private readonly KafkaProducer _producer;
    private readonly KafkaConsumedOffsets? _offsets;
    private readonly ConcurrentQueue<KafkaTransactionalSend> _sends = new();

    public KafkaTransactionBatch(KafkaProducer producer, KafkaConsumedOffsets? offsets)
    {
        _producer = producer;
        _offsets = offsets;
    }

    public void Add(KafkaTransactionalSend send) => _sends.Enqueue(send);

    public Task Commit(CancellationToken ct = default) => _producer.CommitTransactionAsync(_sends.ToArray(), _offsets, ct);

    // The block rolled back before the transaction began: nothing was sent.
    public Task Rollback(CancellationToken ct = default) => Task.CompletedTask;
}
