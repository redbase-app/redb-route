using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using StackExchange.Redis;

namespace redb.Route.Redis;

/// <summary>
/// Redis consumer. Supports three modes based on the operation type:
/// <list type="bullet">
///   <item><see cref="RedisOperationType.SUBSCRIBE"/>/<see cref="RedisOperationType.PSUBSCRIBE"/> —
///     Pub/Sub with <see cref="ISubscriber"/> event-driven callback.</item>
///   <item><see cref="RedisOperationType.XREAD"/>/<see cref="RedisOperationType.XGROUP"/> —
///     Redis Streams polling loop with consumer groups.</item>
///   <item><see cref="RedisOperationType.BLPOP"/>/<see cref="RedisOperationType.BRPOP"/> —
///     Blocking list pop via polling.</item>
/// </list>
/// </summary>
public sealed class RedisConsumer : DrainableConsumer
{
    private readonly RedisEndpoint _endpoint;
    private readonly RedisEndpointOptions _options;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => $"redis:{_endpoint.OperationType}:{_endpoint.Resource}";

    /// <summary>Number of messages successfully processed.</summary>
    public long ProcessedCount { get; private set; }

    /// <summary>Creates a Redis consumer.</summary>
    public RedisConsumer(RedisEndpoint endpoint, IProcessor processor, RedisEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        return _endpoint.OperationType switch
        {
            RedisOperationType.SUBSCRIBE or RedisOperationType.PSUBSCRIBE
                => RunPubSubConsumerAsync(pollCt, processingCt),
            RedisOperationType.XREAD or RedisOperationType.XGROUP
                => RunStreamConsumerAsync(pollCt, processingCt),
            RedisOperationType.BLPOP or RedisOperationType.BRPOP
                => RunListConsumerAsync(pollCt, processingCt),
            _ => throw new NotSupportedException(
                $"Consumer not supported for operation {_endpoint.OperationType}. " +
                "Only SUBSCRIBE/PSUBSCRIBE, XREAD/XGROUP, BLPOP/BRPOP are supported.")
        };
    }

    // ═══════════════════════════════════════════════════════════
    // Pub/Sub
    // ═══════════════════════════════════════════════════════════

    private async Task RunPubSubConsumerAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        var subscriber = await _endpoint.GetSubscriberAsync(processingCt).ConfigureAwait(false);
        var channel = _options.Channel ?? _endpoint.Resource;

        var redisChannel = _options.UsePattern
            ? new RedisChannel(channel, RedisChannel.PatternMode.Pattern)
            : new RedisChannel(channel, RedisChannel.PatternMode.Literal);

        ChannelMessageQueue? queue = null;
        try
        {
            queue = await subscriber.SubscribeAsync(redisChannel).ConfigureAwait(false);

            Logger?.LogInformation("Redis subscribed to channel: {Channel} (pattern={UsePattern})",
                channel, _options.UsePattern);

            while (!pollCt.IsCancellationRequested)
            {
                var msg = await queue.ReadAsync(pollCt).ConfigureAwait(false);
                await ProcessPubSubMessageAsync(msg.Channel!, msg.Message, processingCt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error in Redis Pub/Sub consumer for channel {Channel}", channel);
        }
        finally
        {
            if (queue != null)
            {
                try { await subscriber.UnsubscribeAsync(redisChannel).ConfigureAwait(false); }
                catch (Exception ex) { Logger?.LogWarning(ex, "Error unsubscribing from Redis channel"); }
            }
        }
    }

    private async Task ProcessPubSubMessageAsync(RedisChannel channel, RedisValue message, CancellationToken ct)
    {
        var exchange = Exchange.Create(new Message(message.ToString()), _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOnly;
        IncrementInflight();
        try
        {
            exchange.In.Headers[RedisHeaders.Channel] = channel.ToString();
            exchange.In.Headers[RedisHeaders.MessageType] = "PubSub";
            exchange.In.Headers[RedisHeaders.Timestamp] = DateTimeOffset.UtcNow;

            await Processor.Process(exchange, ct).ConfigureAwait(false);
            ProcessedCount++;
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error processing Pub/Sub message from channel {Channel}", channel);
        }
        finally
        {
            DecrementInflight();
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Streams (XREAD / XGROUP)
    // ═══════════════════════════════════════════════════════════

    private async Task RunStreamConsumerAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        var db = await _endpoint.GetDatabaseAsync(processingCt).ConfigureAwait(false);
        var streamName = _options.StreamName ?? _endpoint.Resource;
        var consumerGroup = _options.ConsumerGroup;
        var consumerName = _options.ConsumerName ?? Environment.MachineName;

        // Create consumer group if needed
        if (!string.IsNullOrEmpty(consumerGroup))
        {
            try
            {
                await db.StreamCreateConsumerGroupAsync(
                    streamName,
                    consumerGroup,
                    _options.StreamStartPosition,
                    createStream: true).ConfigureAwait(false);

                Logger?.LogInformation("Redis stream consumer group created: {Group} for {Stream}",
                    consumerGroup, streamName);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                Logger?.LogDebug("Redis stream consumer group {Group} already exists", consumerGroup);
            }
        }

        Logger?.LogInformation("Redis stream consumer started: stream={Stream}, group={Group}, consumer={Consumer}",
            streamName, consumerGroup, consumerName);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                StreamEntry[] entries;

                if (!string.IsNullOrEmpty(consumerGroup))
                {
                    entries = await db.StreamReadGroupAsync(
                        streamName,
                        consumerGroup,
                        consumerName,
                        ">",
                        _options.StreamReadCount,
                        noAck: !_options.StreamAutoAck).ConfigureAwait(false);
                }
                else
                {
                    entries = await db.StreamReadAsync(
                        streamName,
                        _options.StreamStartPosition,
                        _options.StreamReadCount).ConfigureAwait(false);
                }

                if (entries is { Length: > 0 })
                {
                    foreach (var entry in entries)
                    {
                        await ProcessStreamEntryAsync(entry, db, streamName, consumerGroup, processingCt)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    await Task.Delay(_options.StreamBlockTimeMs, pollCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error reading from Redis stream {Stream}", streamName);
                try { await Task.Delay(_options.PollDelayMs * 5, pollCt).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessStreamEntryAsync(
        StreamEntry entry, IDatabase db, string streamName, string? consumerGroup, CancellationToken ct)
    {
        try
        {
            // Build fields dictionary (raw strings)
            var fields = entry.Values.ToDictionary(
                nv => nv.Name.ToString(),
                nv => (object?)nv.Value.ToString());

            var exchange = Exchange.Create(new Message(fields), _endpoint.ScopeFactory);
            exchange.Pattern = ExchangePattern.InOnly;
            IncrementInflight();
            try
            {
                exchange.In.Headers[RedisHeaders.Stream] = streamName;
                exchange.In.Headers[RedisHeaders.MessageId] = entry.Id.ToString();
                exchange.In.Headers[RedisHeaders.MessageType] = "Stream";
                exchange.In.Headers[RedisHeaders.Timestamp] = DateTimeOffset.UtcNow;

                // Add stream fields as individual headers
                foreach (var nv in entry.Values)
                    exchange.In.Headers[$"{RedisHeaders.StreamFieldPrefix}{nv.Name}"] = nv.Value.ToString();

                // Register stream ack action
                if (!string.IsNullOrEmpty(consumerGroup) && _options.StreamAutoAck)
                {
                    var ackAction = new RedisStreamAckAction(db, streamName, consumerGroup, entry.Id, Logger);
                    RegisterTransactedAction(exchange, $"redis-stream-ack-{entry.Id}", ackAction);
                }

                await Processor.Process(exchange, ct).ConfigureAwait(false);

                // Auto-ack if not using transacted mode
                if (!string.IsNullOrEmpty(consumerGroup) && _options.StreamAutoAck && !_options.Transacted)
                {
                    await db.StreamAcknowledgeAsync(streamName, consumerGroup, entry.Id).ConfigureAwait(false);
                }

                ProcessedCount++;
            }
            finally
            {
                DecrementInflight();
                await exchange.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error processing stream entry {MessageId} from {Stream}", entry.Id, streamName);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // List (BLPOP / BRPOP)
    // ═══════════════════════════════════════════════════════════

    private async Task RunListConsumerAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        var db = await _endpoint.GetDatabaseAsync(processingCt).ConfigureAwait(false);
        var key = _options.Key ?? _endpoint.Resource;

        Logger?.LogInformation("Redis list consumer started: key={Key}, operation={Op}",
            key, _endpoint.OperationType);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                RedisValue result;

                if (_endpoint.OperationType == RedisOperationType.BLPOP)
                    result = await db.ListLeftPopAsync(key).ConfigureAwait(false);
                else
                    result = await db.ListRightPopAsync(key).ConfigureAwait(false);

                if (result.HasValue)
                {
                    await ProcessListMessageAsync(result, key, processingCt).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(_options.PollDelayMs, pollCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error reading from Redis list {Key}", key);
                try { await Task.Delay(_options.PollDelayMs * 5, pollCt).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessListMessageAsync(RedisValue message, string key, CancellationToken ct)
    {
        var exchange = Exchange.Create(new Message(message.ToString()), _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOnly;
        IncrementInflight();
        try
        {
            exchange.In.Headers[RedisHeaders.Key] = key;
            exchange.In.Headers[RedisHeaders.MessageType] = "List";
            exchange.In.Headers[RedisHeaders.Timestamp] = DateTimeOffset.UtcNow;

            await Processor.Process(exchange, ct).ConfigureAwait(false);
            ProcessedCount++;
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error processing list message from {Key}", key);
        }
        finally
        {
            DecrementInflight();
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
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
}

/// <summary>
/// Deferred stream acknowledgement action. Acks on commit, does nothing on rollback (retry via pending list).
/// </summary>
internal sealed class RedisStreamAckAction : ITransactedAction
{
    private readonly IDatabase _db;
    private readonly string _streamName;
    private readonly string _consumerGroup;
    private readonly RedisValue _messageId;
    private readonly ILogger? _logger;

    public RedisStreamAckAction(IDatabase db, string streamName, string consumerGroup, RedisValue messageId, ILogger? logger)
    {
        _db = db;
        _streamName = streamName;
        _consumerGroup = consumerGroup;
        _messageId = messageId;
        _logger = logger;
    }

    public async Task Commit(CancellationToken ct = default)
    {
        await _db.StreamAcknowledgeAsync(_streamName, _consumerGroup, _messageId).ConfigureAwait(false);
        _logger?.LogDebug("Redis stream ack committed: {Stream}/{MessageId}", _streamName, _messageId);
    }

    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("Redis stream ack rolled back (message remains pending): {Stream}/{MessageId}",
            _streamName, _messageId);
        return Task.CompletedTask;
    }
}
