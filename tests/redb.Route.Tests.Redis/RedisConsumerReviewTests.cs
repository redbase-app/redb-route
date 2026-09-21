using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Redis;
using StackExchange.Redis;

namespace redb.Route.Tests.Redis;

/// <summary>
/// The consumer side of the Redis connector review (docs/BUG_REDIS_CONNECTOR_REVIEW_2026_09_18.md, items 1, 2, 3, 7, 8):
/// PSUBSCRIBE subscribes to a pattern, XREAD without a group moves on through the stream, a group consumer acknowledges
/// after the route and says NOACK when it means it, a failed stream entry is claimed again, and a list consumer with a
/// processing list loses nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisConsumerReviewTests
{
    private const string ConnectionString = "localhost:6379";

    private static RedisEndpoint Endpoint(string path, string? parameters = null)
    {
        var query = $"connectionString={ConnectionString}";
        if (parameters is not null) query += $"&{parameters}";
        return (RedisEndpoint)new RedisComponent().CreateEndpoint(EndpointUriParser.Parse($"redis:{path}?{query}"));
    }

    private static async Task<IDatabase> Database() =>
        (await ConnectionMultiplexer.ConnectAsync(ConnectionString)).GetDatabase();

    private sealed class Recorder : IProcessor
    {
        private readonly Func<IExchange, int, bool> _succeeds;
        private int _calls;

        public Recorder(Func<IExchange, int, bool>? succeeds = null) => _succeeds = succeeds ?? ((_, _) => true);

        public readonly ConcurrentQueue<string> Bodies = new();

        public int Calls => Volatile.Read(ref _calls);

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            Bodies.Enqueue(exchange.In.Body switch
            {
                IDictionary<string, object?> fields => string.Join(",", fields.Values),
                var body => body?.ToString() ?? "",
            });
            return _succeeds(exchange, call)
                ? Task.CompletedTask
                : throw new InvalidOperationException($"processing failed on call {call}");
        }
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    private static async Task<(RedisConsumer Consumer, Recorder Recorder)> Consume(
        string path, string? parameters, Func<IExchange, int, bool>? succeeds = null)
    {
        var recorder = new Recorder(succeeds);
        var consumer = (RedisConsumer)Endpoint(path, parameters).CreateConsumer(recorder);
        await consumer.Start();
        return (consumer, recorder);
    }

    // ── Item 2: PSUBSCRIBE ──

    [Fact]
    public async Task PSUBSCRIBE_subscribes_to_the_pattern_without_usePattern()
    {
        var prefix = $"psub-{Guid.NewGuid():N}";
        var (consumer, recorder) = await Consume($"PSUBSCRIBE:{prefix}.*", null);
        await Task.Delay(500);

        await (await Database()).PublishAsync(RedisChannel.Literal($"{prefix}.orders"), "matched");
        await Until(() => recorder.Calls > 0, 5_000);
        await consumer.Stop();

        recorder.Bodies.Should().Equal("matched");
    }

    // ── Item 1: XREAD without a group ──

    [Fact]
    public async Task XREAD_without_a_group_reads_each_entry_once()
    {
        var stream = $"xread-once-{Guid.NewGuid():N}";
        var db = await Database();
        foreach (var n in new[] { "a", "b", "c" })
            await db.StreamAddAsync(stream, "n", n);

        var (consumer, recorder) = await Consume($"XREAD:{stream}", "streamStartPosition=0&streamBlockTimeMs=50");
        await Until(() => recorder.Calls >= 3);
        await Task.Delay(1_000);
        await consumer.Stop();

        recorder.Bodies.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task XREAD_without_a_group_starts_at_the_entries_added_after_it_started()
    {
        var stream = $"xread-new-{Guid.NewGuid():N}";
        var db = await Database();
        await db.StreamAddAsync(stream, "n", "before");

        var (consumer, recorder) = await Consume($"XREAD:{stream}", "streamBlockTimeMs=50");
        await Task.Delay(500);
        await db.StreamAddAsync(stream, "n", "after");
        await Until(() => recorder.Calls > 0, 5_000);
        await Task.Delay(300);
        await consumer.Stop();

        recorder.Bodies.Should().Equal("after");
    }

    [Fact]
    public void XREAD_without_a_group_refuses_the_group_only_position()
    {
        var endpoint = Endpoint($"XREAD:s-{Guid.NewGuid():N}");
        endpoint.EndpointOptions.StreamStartPosition = ">";

        var act = () => endpoint.CreateConsumer(new Recorder());

        act.Should().Throw<ArgumentException>().WithMessage("*streamStartPosition=>*no consumer group*");
    }

    // ── Item 3: acknowledgement ──

    [Fact]
    public async Task A_group_consumer_leaves_a_failed_entry_pending()
    {
        var stream = $"ack-{Guid.NewGuid():N}";
        var group = $"g-{Guid.NewGuid():N}";
        var db = await Database();
        await db.StreamAddAsync(stream, "n", "x");

        var (consumer, recorder) = await Consume($"XGROUP:{stream}",
            $"consumerGroup={group}&streamStartPosition=0&streamBlockTimeMs=50", (_, _) => false);
        await Until(() => recorder.Calls > 0);
        await Task.Delay(300);
        await consumer.Stop();

        (await db.StreamPendingAsync(stream, group)).PendingMessageCount.Should().Be(1,
            "the entry is acknowledged only after the route succeeded");
    }

    [Fact]
    public async Task StreamNoAck_reads_with_NOACK_and_leaves_nothing_pending()
    {
        var stream = $"noack-{Guid.NewGuid():N}";
        var group = $"g-{Guid.NewGuid():N}";
        var db = await Database();
        await db.StreamAddAsync(stream, "n", "x");

        var (consumer, recorder) = await Consume($"XGROUP:{stream}",
            $"consumerGroup={group}&streamStartPosition=0&streamBlockTimeMs=50&streamNoAck=true", (_, _) => false);
        await Until(() => recorder.Calls > 0);
        await Task.Delay(300);
        await consumer.Stop();

        (await db.StreamPendingAsync(stream, group)).PendingMessageCount.Should().Be(0,
            "NOACK: the entry counts as delivered when it is read, failed or not");
    }

    [Fact]
    public void The_former_streamAutoAck_is_refused_with_its_replacement()
    {
        var act = () => Endpoint($"XGROUP:s-{Guid.NewGuid():N}", "consumerGroup=g&streamAutoAck=false");

        act.Should().Throw<ArgumentException>().WithMessage("*streamAutoAck*streamNoAck*");
    }

    [Fact]
    public void The_builder_writes_the_new_consumer_options_out()
    {
        var uri = redb.Route.Redis.Redis.XGroup("s").ConsumerGroup("g").StreamNoAck().Build();
        uri.Should().Contain("streamNoAck=true");

        redb.Route.Redis.Redis.XGroup("s").ConsumerGroup("g").StreamClaimMinIdle(500).Build()
            .Should().Contain("streamClaimMinIdleMs=500").And.NotContain("streamNoAck");
        var list = redb.Route.Redis.Redis.Command("BLPOP", "q").ProcessingList("q:processing").Build();
        ((RedisEndpoint)new RedisComponent().CreateEndpoint(EndpointUriParser.Parse(list)))
            .EndpointOptions.ProcessingList.Should().Be("q:processing");
    }

    [Fact]
    public void A_misspelt_option_is_refused_by_name()
    {
        var act = () => Endpoint($"SET:k-{Guid.NewGuid():N}", "tll=5");

        act.Should().Throw<ArgumentException>().WithMessage("*'tll' is not an option*");
    }

    // ── Item 8: pending entries are claimed again ──

    [Fact]
    public async Task A_failed_entry_is_claimed_again_once_it_has_idled()
    {
        var stream = $"claim-{Guid.NewGuid():N}";
        var group = $"g-{Guid.NewGuid():N}";
        var db = await Database();
        await db.StreamAddAsync(stream, "n", "retry-me");

        var (consumer, recorder) = await Consume($"XGROUP:{stream}",
            $"consumerGroup={group}&streamStartPosition=0&streamBlockTimeMs=50&streamClaimMinIdleMs=300",
            (_, call) => call > 1);
        await Until(() => recorder.Calls >= 2);
        await Task.Delay(300);
        await consumer.Stop();

        recorder.Bodies.Should().Equal("retry-me", "retry-me");
        (await db.StreamPendingAsync(stream, group)).PendingMessageCount.Should().Be(0);
    }

    // ── Item 7: a list consumer with a processing list ──

    [Fact]
    public async Task With_a_processing_list_a_failed_item_goes_back_and_is_processed_again()
    {
        var key = $"queue-{Guid.NewGuid():N}";
        var processing = $"{key}:processing";
        var db = await Database();
        await db.ListRightPushAsync(key, "job-1");

        var (consumer, recorder) = await Consume($"BLPOP:{key}",
            $"processingList={processing}&pollDelayMs=50", (_, call) => call > 1);
        await Until(() => recorder.Calls >= 2);
        await Task.Delay(300);
        await consumer.Stop();

        recorder.Bodies.Should().Equal("job-1", "job-1");
        (await db.ListLengthAsync(key)).Should().Be(0);
        (await db.ListLengthAsync(processing)).Should().Be(0, "a processed item leaves the processing list");
    }

    [Fact]
    public async Task What_a_previous_run_left_in_the_processing_list_is_processed_at_start()
    {
        var key = $"queue-{Guid.NewGuid():N}";
        var processing = $"{key}:processing";
        var db = await Database();
        await db.ListRightPushAsync(processing, "orphan");

        var (consumer, recorder) = await Consume($"BLPOP:{key}", $"processingList={processing}&pollDelayMs=50");
        await Until(() => recorder.Calls >= 1);
        await Task.Delay(300);
        await consumer.Stop();

        recorder.Bodies.Should().Equal("orphan");
        (await db.ListLengthAsync(processing)).Should().Be(0);
    }
}
