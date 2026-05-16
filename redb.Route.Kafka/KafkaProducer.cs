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
/// Kafka producer. Sends messages to a Kafka topic with support for transactional
/// deferred-commit via <see cref="ITransactedAction"/>.
/// </summary>
public sealed class KafkaProducer : ConnectableProducer
{
    private readonly KafkaEndpoint _endpoint;
    private readonly KafkaEndpointOptions _options;
    private IProducer<string, byte[]>? _producer;

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
    protected override Task ConnectAsync(CancellationToken ct)
    {
        var config = _options.BuildProducerConfig(_endpoint.ResolvedFactory);

        _producer = new ProducerBuilder<string, byte[]>(config)
            .SetValueSerializer(Serializers.ByteArray)
            .SetErrorHandler((_, e) =>
                Logger?.LogError("Kafka producer error: {Reason} (Code: {Code})", e.Reason, e.Code))
            .Build();

        try
        {
            if (_options.Transacted)
            {
                _producer.InitTransactions(TimeSpan.FromSeconds(30));
                Logger?.LogInformation("Kafka transactional mode enabled: topic={Topic}", _endpoint.TopicName);
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Kafka producer ConnectAsync failed for topic {Topic}", _endpoint.TopicName);
            _producer?.Dispose();
            _producer = null;
            throw;
        }

        return Task.CompletedTask;
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

        using var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.TopicName} publish", ActivityKind.Producer);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "kafka");
            activity.SetTag("messaging.operation", "publish");
            activity.SetTag("messaging.destination.name", _endpoint.TopicName);
        }

        var message = PrepareMessage(exchange);

        // Inject W3C trace context into Kafka headers
        InjectTraceContext(activity, message.Headers);

        if (_options.Transacted)
        {
            // Deferred transactional send — actual publish happens on commit
            var action = new KafkaSendAction(_producer, _endpoint.TopicName, message,
                _options.PartitionNumber, _options.RecordMetadata, exchange, Logger);
            RegisterTransactedAction(exchange, $"kafka-send-{Guid.NewGuid():N}", action);
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

            if (_options.RecordMetadata)
                AddDeliveryMetadata(exchange, result);
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
        if (!string.IsNullOrEmpty(exchange.In.ContentType))
        {
            msg.Headers.Add("content-type", Encoding.UTF8.GetBytes(exchange.In.ContentType));
        }

        // Propagate headers (skip kafka.* internal ones)
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (KafkaHeaders.IsRedbHeader(key))
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

    /// <summary>
    /// Injects W3C trace context (traceparent/tracestate) into Kafka message headers
    /// using the .NET built-in DistributedContextPropagator.
    /// </summary>
    private static void InjectTraceContext(Activity? activity, Headers headers)
    {
        if (activity is null) return;

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, headers, static (carrier, key, value) =>
        {
            if (carrier is Headers h && !string.IsNullOrEmpty(value))
            {
                h.Remove(key);
                h.Add(key, Encoding.UTF8.GetBytes(value));
            }
        });
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

        return null;
    }

    private static void AddDeliveryMetadata(IExchange exchange, DeliveryResult<string, byte[]> result)
    {
        exchange.In.Headers[KafkaHeaders.SentTopic] = result.Topic;
        exchange.In.Headers[KafkaHeaders.SentPartition] = result.Partition.Value;
        exchange.In.Headers[KafkaHeaders.SentOffset] = result.Offset.Value;
        exchange.In.Headers[KafkaHeaders.SentTimestamp] = DateTimeOffset.UtcNow;
    }

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
}

/// <summary>
/// Deferred Kafka send action. The message is captured at creation time
/// and published only on <see cref="Commit"/>. On <see cref="Rollback"/> the message is discarded.
/// </summary>
internal sealed class KafkaSendAction : ITransactedAction
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly string _topicName;
    private readonly Message<string, byte[]> _message;
    private readonly int? _partition;
    private readonly bool _recordMetadata;
    private readonly IExchange _exchange;
    private readonly ILogger? _logger;

    public KafkaSendAction(
        IProducer<string, byte[]> producer,
        string topicName,
        Message<string, byte[]> message,
        int? partition,
        bool recordMetadata,
        IExchange exchange,
        ILogger? logger)
    {
        _producer = producer;
        _topicName = topicName;
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

        if (_recordMetadata)
        {
            _exchange.In.Headers[KafkaHeaders.SentTopic] = result.Topic;
            _exchange.In.Headers[KafkaHeaders.SentPartition] = result.Partition.Value;
            _exchange.In.Headers[KafkaHeaders.SentOffset] = result.Offset.Value;
        }
    }

    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("Kafka transactional send rolled back (message discarded): topic={Topic}", _topicName);
        return Task.CompletedTask;
    }

    private static Message<string, byte[]> CloneMessage(Message<string, byte[]> source)
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
