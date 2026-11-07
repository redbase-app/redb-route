using System.Collections.Concurrent;
using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Redis;
using StackExchange.Redis;

namespace redb.Route.Tests.Redis;

/// <summary>
/// A started Redis consumer receives: the subscription, the consumer group and the XREAD position are in place when
/// Start returns. They used to be set up in the background loop after it, so a message published or an entry added right
/// after Start was lost. A Redis that does not answer fails Start instead of leaving a consumer that never receives.
/// No pause between Start and the send anywhere here: that pause is what hid the loss. Requires Redis at localhost:6379.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisConsumerStartTests
{
    private const string ConnectionString = "localhost:6379";

    private sealed class Recorder : IProcessor
    {
        public readonly ConcurrentQueue<string> Bodies = new();

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            Bodies.Enqueue(exchange.In.Body switch
            {
                IDictionary<string, object?> fields => string.Join(",", fields.Values),
                var body => body?.ToString() ?? "",
            });
            return Task.CompletedTask;
        }
    }

    private static RedisConsumer Consumer(string path, Recorder recorder, string connectionString = ConnectionString,
        string? parameters = null)
    {
        var query = $"connectionString={Uri.EscapeDataString(connectionString)}";
        if (parameters is not null) query += $"&{parameters}";
        var endpoint = (RedisEndpoint)new RedisComponent().CreateEndpoint(EndpointUriParser.Parse($"redis:{path}?{query}"));
        return (RedisConsumer)endpoint.CreateConsumer(recorder);
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    [Theory]
    [InlineData("SUBSCRIBE", "start-sub-{0}", "start-sub-{0}")]
    [InlineData("PSUBSCRIBE", "start-psub-{0}.*", "start-psub-{0}.orders")]
    public async Task A_message_published_right_after_Start_is_received(string operation, string channel, string target)
    {
        var id = Guid.NewGuid().ToString("N");
        var recorder = new Recorder();
        var consumer = Consumer($"{operation}:{string.Format(channel, id)}", recorder);
        await consumer.Start();
        try
        {
            var db = await TestRedis.Database();
            var receivers = await db.PublishAsync(RedisChannel.Literal(string.Format(target, id)), "first");

            receivers.Should().Be(1, "the subscription exists once Start has returned");
            await Until(() => !recorder.Bodies.IsEmpty);
            recorder.Bodies.Should().Equal("first");
        }
        finally
        {
            await consumer.Stop();
        }
    }

    [Fact]
    public async Task An_entry_added_right_after_Start_is_read_and_one_added_before_is_not()
    {
        var stream = $"start-xread-{Guid.NewGuid():N}";
        var db = await TestRedis.Database();
        await db.StreamAddAsync(stream, "n", "before");
        var recorder = new Recorder();
        var consumer = Consumer($"XREAD:{stream}", recorder, parameters: "streamBlockTimeMs=50");
        await consumer.Start();
        try
        {
            await db.StreamAddAsync(stream, "n", "after");

            await Until(() => !recorder.Bodies.IsEmpty);
            await Task.Delay(300);
            recorder.Bodies.Should().Equal(["after"], "the position is pinned at Start, to the entries added from then on");
        }
        finally
        {
            await consumer.Stop();
        }
    }

    [Fact]
    public async Task A_group_consumer_reads_an_entry_added_right_after_Start()
    {
        var stream = $"start-xgroup-{Guid.NewGuid():N}";
        var recorder = new Recorder();
        var consumer = Consumer($"XGROUP:{stream}", recorder, parameters: "consumerGroup=g1&streamBlockTimeMs=50");
        await consumer.Start();
        try
        {
            await (await TestRedis.Database()).StreamAddAsync(stream, "n", "after");

            await Until(() => !recorder.Bodies.IsEmpty);
            recorder.Bodies.Should().Equal("after");
        }
        finally
        {
            await consumer.Stop();
        }
    }

    [Theory]
    [InlineData("SUBSCRIBE:start-down")]
    [InlineData("XREAD:start-down")]
    [InlineData("XGROUP:start-down")]
    [InlineData("BLPOP:start-down")]
    public async Task A_Redis_that_does_not_answer_fails_Start_and_names_the_consumer(string path)
    {
        // Port 1: nothing listens there. Short timeouts keep the test quick; the defaults fail the same way, later.
        var consumer = Consumer(path, new Recorder(), "127.0.0.1:1,connectTimeout=500,asyncTimeout=1000",
            path.StartsWith("XGROUP", StringComparison.Ordinal) ? "consumerGroup=g1" : null);
        var watch = Stopwatch.StartNew();

        var start = () => consumer.Start();

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*redis:{path}*cannot start*")
            .Which.InnerException.Should().Match<Exception>(e => e is RedisException || e is TimeoutException);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }
}
