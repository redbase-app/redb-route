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
///     Redis Streams polling loop: with a consumer group, acknowledged after the route (or NOACK) and pending entries
///     claimed again after an idle time; without one, a position of its own that moves past every entry read.</item>
///   <item><see cref="RedisOperationType.BLPOP"/>/<see cref="RedisOperationType.BRPOP"/> —
///     list pop via polling (a blocking pop would hold the shared connection); at-least-once with a processing list.</item>
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

        // PSUBSCRIBE is a pattern subscription by itself; usePattern makes SUBSCRIBE one too.
        var pattern = _endpoint.OperationType == RedisOperationType.PSUBSCRIBE || _options.UsePattern;
        var redisChannel = pattern
            ? new RedisChannel(channel, RedisChannel.PatternMode.Pattern)
            : new RedisChannel(channel, RedisChannel.PatternMode.Literal);

        ChannelMessageQueue? queue = null;
        try
        {
            queue = await subscriber.SubscribeAsync(redisChannel).ConfigureAwait(false);

            Logger?.LogInformation("Redis subscribed to channel: {Channel} (pattern={Pattern})", channel, pattern);

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
            exchange.ThrowIfUnhandledFailure();
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
                // Unset position: the group starts at the stream's end ($), as StackExchange.Redis does by default.
                await db.StreamCreateConsumerGroupAsync(
                    streamName,
                    consumerGroup,
                    _options.StreamStartPosition is { } start ? start : null,
                    createStream: true).ConfigureAwait(false);

                Logger?.LogInformation("Redis stream consumer group created: {Group} for {Stream}",
                    consumerGroup, streamName);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                Logger?.LogDebug("Redis stream consumer group {Group} already exists", consumerGroup);
            }
        }

        // Without a group the position is this consumer's to keep, and it moves past every entry read.
        var position = string.IsNullOrEmpty(consumerGroup)
            ? await StartPositionAsync(db, streamName).ConfigureAwait(false)
            : default;
        RedisValue claimCursor = "0-0";

        Logger?.LogInformation("Redis stream consumer started: stream={Stream}, group={Group}, consumer={Consumer}",
            streamName, consumerGroup, consumerName);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                StreamEntry[] entries;
                var claimedAny = false;

                if (!string.IsNullOrEmpty(consumerGroup))
                {
                    if (_options.StreamClaimMinIdleMs is { } minIdle)
                    {
                        // Entries of the group pending for longer than minIdle — a failed entry of this consumer, or one
                        // a dead consumer left behind — are taken over and processed again. The cursor walks the
                        // pending list and comes back to the start once it has seen all of it.
                        var claimed = await db.StreamAutoClaimAsync(
                            streamName, consumerGroup, consumerName, minIdle, claimCursor, _options.StreamReadCount)
                            .ConfigureAwait(false);
                        claimCursor = claimed.NextStartId;
                        foreach (var entry in claimed.ClaimedEntries.Where(e => !e.IsNull))
                        {
                            claimedAny = true;
                            await ProcessStreamEntryAsync(entry, db, streamName, consumerGroup, processingCt)
                                .ConfigureAwait(false);
                        }
                    }

                    entries = await db.StreamReadGroupAsync(
                        streamName,
                        consumerGroup,
                        consumerName,
                        StreamPosition.NewMessages,
                        _options.StreamReadCount,
                        noAck: _options.StreamNoAck).ConfigureAwait(false);
                }
                else
                {
                    entries = await db.StreamReadAsync(
                        streamName,
                        position,
                        _options.StreamReadCount).ConfigureAwait(false);
                }

                if (entries is { Length: > 0 })
                {
                    foreach (var entry in entries)
                    {
                        await ProcessStreamEntryAsync(entry, db, streamName, consumerGroup, processingCt)
                            .ConfigureAwait(false);
                        // Past a failed entry too: without a group nothing keeps it pending, as a Kafka consumer
                        // without breakOnFirstError moves on.
                        if (string.IsNullOrEmpty(consumerGroup))
                            position = entry.Id;
                    }
                }
                else if (!claimedAny)
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

    /// <summary>
    /// Where an XREAD consumer without a group starts. Unset or <c>$</c>: the entries added from now on. XREAD takes
    /// <c>$</c> as "after the last entry at the time of this call", which a polling loop would evaluate again on every
    /// call and so skip whatever arrived in between: it is pinned once, to the id of the stream's last entry, or to the
    /// start of an empty stream. Anything else (<c>0</c>, an entry id) is taken as given.
    /// </summary>
    private async Task<RedisValue> StartPositionAsync(IDatabase db, string streamName)
    {
        if (_options.StreamStartPosition is not (null or "$"))
            return _options.StreamStartPosition;

        var last = await db.StreamRangeAsync(streamName, "-", "+", count: 1, messageOrder: Order.Descending)
            .ConfigureAwait(false);
        return last.Length > 0 ? last[0].Id : "0-0";
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

                // The entry is acknowledged by this consumer after the unit of work ended well (below), not by
                // the route transaction: the transaction owns the database and the outgoing sends.

                await Processor.Process(exchange, ct).ConfigureAwait(false);
                exchange.ThrowIfUnhandledFailure();

                // Acknowledge the entry now that the whole unit of work, the route transaction included, is done. A
                // failed entry is not acknowledged: it stays pending, for streamClaimMinIdleMs to claim again.
                if (!string.IsNullOrEmpty(consumerGroup) && !_options.StreamNoAck)
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

        // BLPOP takes from the head, BRPOP from the tail. Both poll: a blocking pop would hold the multiplexed
        // connection every other command of this process shares.
        var side = _endpoint.OperationType == RedisOperationType.BLPOP ? ListSide.Left : ListSide.Right;
        var processing = _options.ProcessingList;

        Logger?.LogInformation("Redis list consumer started: key={Key}, operation={Op}, processing list={Processing}",
            key, _endpoint.OperationType, processing);

        if (processing is not null)
            await ReturnLeftoversAsync(db, processing, key, side).ConfigureAwait(false);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                // With a processing list the item is moved there (LMOVE) and leaves it only once processed; without one
                // it is popped, and a failed item is gone.
                var result = processing is null
                    ? side == ListSide.Left
                        ? await db.ListLeftPopAsync(key).ConfigureAwait(false)
                        : await db.ListRightPopAsync(key).ConfigureAwait(false)
                    : await db.ListMoveAsync(key, processing, side, ListSide.Left).ConfigureAwait(false);

                if (!result.HasValue)
                {
                    await Task.Delay(_options.PollDelayMs, pollCt).ConfigureAwait(false);
                    continue;
                }

                var processed = await ProcessListMessageAsync(result, key, processingCt).ConfigureAwait(false);
                if (processing is null)
                    continue;

                if (processed)
                {
                    await db.ListRemoveAsync(processing, result, count: 1).ConfigureAwait(false);
                }
                else
                {
                    // Back to the side it was taken from, so it is the next one taken; atomically, so a crash in
                    // between cannot leave it in both lists or in neither.
                    var back = db.CreateTransaction();
                    _ = back.ListRemoveAsync(processing, result, count: 1);
                    _ = side == ListSide.Left ? back.ListLeftPushAsync(key, result) : back.ListRightPushAsync(key, result);
                    await back.ExecuteAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Returns what a previous run left in the processing list — items it took and never finished — to the side of the
    /// queue this consumer takes from, oldest first.
    /// </summary>
    private async Task ReturnLeftoversAsync(IDatabase db, string processing, string key, ListSide side)
    {
        var returned = 0;
        // The newest item sits at the head of the processing list; moving from the head to the taking side, one by one,
        // leaves the oldest nearest to it.
        while ((await db.ListMoveAsync(processing, key, ListSide.Left, side).ConfigureAwait(false)).HasValue)
            returned++;
        if (returned > 0)
            Logger?.LogWarning("Redis list consumer returned {Count} unfinished item(s) from {Processing} to {Key}",
                returned, processing, key);
    }

    /// <returns>Whether the route succeeded with the item.</returns>
    private async Task<bool> ProcessListMessageAsync(RedisValue message, string key, CancellationToken ct)
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
            exchange.ThrowIfUnhandledFailure();
            ProcessedCount++;
            return true;
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error processing list message from {Key}", key);
            return false;
        }
        finally
        {
            DecrementInflight();
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

}
