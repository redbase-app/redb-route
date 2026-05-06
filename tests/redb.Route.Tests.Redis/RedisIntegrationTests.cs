using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Redis;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace redb.Route.Tests.Redis;

/// <summary>
/// Integration tests against a real Redis instance.
/// Expects Redis at localhost:6379 (no password).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisIntegrationTests
{
    private const string ConnectionString = "localhost:6379";
    private readonly ITestOutputHelper _output;

    public RedisIntegrationTests(ITestOutputHelper output) => _output = output;

    // ───── Helpers ─────

    private RedisEndpoint CreateEndpoint(string path, string? extraParams = null)
    {
        var qs = $"connectionString={ConnectionString}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"redis:{path}?{qs}");
        var component = new RedisComponent();
        return (RedisEndpoint)component.CreateEndpoint(uri);
    }

    private async Task<IDatabase> GetDatabase()
    {
        var conn = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        return conn.GetDatabase();
    }

    // ═══════════════════════════════════════════════════════════
    // Key/Value (SET, GET, DEL, EXISTS, INCR, DECR, SETNX)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task SetAndGet_Roundtrip()
    {
        var key = $"test:{Guid.NewGuid():N}";

        var epSet = CreateEndpoint($"SET:{key}");
        var producer = (RedisProducer)epSet.CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("Hello Redis"));
        await producer.Process(exchange);
        await producer.Stop();

        var epGet = CreateEndpoint($"GET:{key}");
        var getProducer = (RedisProducer)epGet.CreateProducer();
        await getProducer.Start();
        var getExchange = new Exchange(new Message());
        await getProducer.Process(getExchange);
        await getProducer.Stop();

        getExchange.Out.Should().NotBeNull();
        getExchange.Out!.Body?.ToString().Should().Be("Hello Redis");
    }

    [Fact]
    public async Task Set_WithTtl_ExpiresKey()
    {
        var key = $"test-ttl:{Guid.NewGuid():N}";

        var ep = CreateEndpoint($"SET:{key}", "ttl=1");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("expires-soon")));
        await producer.Stop();

        var db = await GetDatabase();
        (await db.StringGetAsync(key)).ToString().Should().Be("expires-soon");

        await Task.Delay(1500);
        (await db.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task Del_RemovesKey()
    {
        var key = $"test-del:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.StringSetAsync(key, "to-delete");

        var ep = CreateEndpoint($"DEL:{key}");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message()));
        await producer.Stop();

        (await db.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task Exists_ReturnsBoolean()
    {
        var key = $"test-exists:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.StringSetAsync(key, "here");

        var ep = CreateEndpoint($"EXISTS:{key}");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        exchange.Out!.Body.Should().Be(true);
    }

    [Fact]
    public async Task IncrAndDecr_WorkCorrectly()
    {
        var key = $"test-incr:{Guid.NewGuid():N}";
        var db = await GetDatabase();

        var epIncr = CreateEndpoint($"INCR:{key}");
        var incr = (RedisProducer)epIncr.CreateProducer();
        await incr.Start();
        for (int i = 0; i < 3; i++)
            await incr.Process(new Exchange(new Message()));
        await incr.Stop();

        (await db.StringGetAsync(key)).Should().Be("3");

        var epDecr = CreateEndpoint($"DECR:{key}");
        var decr = (RedisProducer)epDecr.CreateProducer();
        await decr.Start();
        await decr.Process(new Exchange(new Message()));
        await decr.Stop();

        (await db.StringGetAsync(key)).Should().Be("2");
    }

    [Fact]
    public async Task Setnx_OnlyIfNotExists()
    {
        var key = $"test-setnx:{Guid.NewGuid():N}";

        var ep1 = CreateEndpoint($"SETNX:{key}");
        var prod1 = (RedisProducer)ep1.CreateProducer();
        await prod1.Start();
        var ex1 = new Exchange(new Message("first"));
        await prod1.Process(ex1);
        await prod1.Stop();
        ex1.Out!.Body?.ToString().Should().Be("OK");

        var ep2 = CreateEndpoint($"SETNX:{key}");
        var prod2 = (RedisProducer)ep2.CreateProducer();
        await prod2.Start();
        var ex2 = new Exchange(new Message("second"));
        await prod2.Process(ex2);
        await prod2.Stop();
        ex2.Out!.Body.Should().BeNull();

        var db = await GetDatabase();
        (await db.StringGetAsync(key)).Should().Be("first");
    }

    // ═══════════════════════════════════════════════════════════
    // Lists
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task List_PushAndPop()
    {
        var key = $"test-list:{Guid.NewGuid():N}";

        var epPush = CreateEndpoint($"LPUSH:{key}");
        var pushProd = (RedisProducer)epPush.CreateProducer();
        await pushProd.Start();
        foreach (var item in new[] { "a", "b", "c" })
            await pushProd.Process(new Exchange(new Message(item)));
        await pushProd.Stop();

        var epPop = CreateEndpoint($"RPOP:{key}");
        var popProd = (RedisProducer)epPop.CreateProducer();
        await popProd.Start();
        var popExchange = new Exchange(new Message());
        await popProd.Process(popExchange);
        await popProd.Stop();

        popExchange.Out!.Body?.ToString().Should().Be("a");

        var epLen = CreateEndpoint($"LLEN:{key}");
        var lenProd = (RedisProducer)epLen.CreateProducer();
        await lenProd.Start();
        var lenExchange = new Exchange(new Message());
        await lenProd.Process(lenExchange);
        await lenProd.Stop();

        ((long)lenExchange.Out!.Body!).Should().Be(2);
    }

    // ═══════════════════════════════════════════════════════════
    // Hashes
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Hash_SetAndGet()
    {
        var key = $"test-hash:{Guid.NewGuid():N}";

        var epSet = CreateEndpoint($"HSET:{key}", "field=name");
        var setProd = (RedisProducer)epSet.CreateProducer();
        await setProd.Start();
        await setProd.Process(new Exchange(new Message("John")));
        await setProd.Stop();

        var epGet = CreateEndpoint($"HGET:{key}", "field=name");
        var getProd = (RedisProducer)epGet.CreateProducer();
        await getProd.Start();
        var getEx = new Exchange(new Message());
        await getProd.Process(getEx);
        await getProd.Stop();

        getEx.Out!.Body?.ToString().Should().Be("John");
    }

    [Fact]
    public async Task Hash_GetAll()
    {
        var key = $"test-hall:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.HashSetAsync(key, new HashEntry[] { new("a", "1"), new("b", "2") });

        var ep = CreateEndpoint($"HGETALL:{key}");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message());
        await prod.Process(ex);
        await prod.Stop();

        var dict = ex.Out!.Body as Dictionary<string, object?>;
        dict.Should().NotBeNull();
        dict!["a"].Should().Be("1");
        dict["b"].Should().Be("2");
    }

    // ═══════════════════════════════════════════════════════════
    // Sets
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_AddAndMembers()
    {
        var key = $"test-set:{Guid.NewGuid():N}";

        var epAdd = CreateEndpoint($"SADD:{key}");
        var addProd = (RedisProducer)epAdd.CreateProducer();
        await addProd.Start();
        foreach (var item in new[] { "x", "y", "z" })
            await addProd.Process(new Exchange(new Message(item)));
        await addProd.Stop();

        var epMembers = CreateEndpoint($"SMEMBERS:{key}");
        var memProd = (RedisProducer)epMembers.CreateProducer();
        await memProd.Start();
        var ex = new Exchange(new Message());
        await memProd.Process(ex);
        await memProd.Stop();

        var members = ex.Out!.Body as string[];
        members.Should().NotBeNull();
        members!.Should().HaveCount(3);
        members.Should().Contain(new[] { "x", "y", "z" });

        var epCard = CreateEndpoint($"SCARD:{key}");
        var cardProd = (RedisProducer)epCard.CreateProducer();
        await cardProd.Start();
        var cardEx = new Exchange(new Message());
        await cardProd.Process(cardEx);
        await cardProd.Stop();

        ((long)cardEx.Out!.Body!).Should().Be(3);
    }

    // ═══════════════════════════════════════════════════════════
    // Sorted Sets
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task SortedSet_AddAndRange()
    {
        var key = $"test-zset:{Guid.NewGuid():N}";

        foreach (var (member, score) in new[] { ("alice", 100.0), ("bob", 200.0), ("carol", 150.0) })
        {
            var epAdd = CreateEndpoint($"ZADD:{key}", $"score={score}");
            var addProd = (RedisProducer)epAdd.CreateProducer();
            await addProd.Start();
            await addProd.Process(new Exchange(new Message(member)));
            await addProd.Stop();
        }

        var epRange = CreateEndpoint($"ZRANGE:{key}", "start=0&stop=-1");
        var rangeProd = (RedisProducer)epRange.CreateProducer();
        await rangeProd.Start();
        var ex = new Exchange(new Message());
        await rangeProd.Process(ex);
        await rangeProd.Stop();

        var ranked = ex.Out!.Body as string[];
        ranked.Should().NotBeNull();
        ranked!.Should().HaveCount(3);
        ranked[0].Should().Be("alice");
        ranked[1].Should().Be("carol");
        ranked[2].Should().Be("bob");
    }

    // ═══════════════════════════════════════════════════════════
    // Pub/Sub
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PubSub_PublishAndSubscribe()
    {
        var channel = $"test-channel-{Guid.NewGuid():N}";
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var epSub = CreateEndpoint($"SUBSCRIBE:{channel}");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                received.Add(ex.In.Body?.ToString() ?? "");
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RedisConsumer)epSub.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(500); // Give subscriber time to connect

        var epPub = CreateEndpoint($"PUBLISH:{channel}");
        var pubProd = (RedisProducer)epPub.CreateProducer();
        await pubProd.Start();
        await pubProd.Process(new Exchange(new Message("pub-sub-test")));
        await pubProd.Stop();

        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().Contain("pub-sub-test");
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ═══════════════════════════════════════════════════════════
    // Streams
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Stream_AddAndRead()
    {
        var stream = $"test-stream-{Guid.NewGuid():N}";
        var group = $"test-grp-{Guid.NewGuid():N}";

        var epAdd = CreateEndpoint($"XADD:{stream}");
        var addProd = (RedisProducer)epAdd.CreateProducer();
        await addProd.Start();
        var addEx = new Exchange(new Message("stream-data"));
        await addProd.Process(addEx);
        await addProd.Stop();

        addEx.Out!.Body?.ToString().Should().NotBeNullOrEmpty();
        addEx.In.Headers.Should().ContainKey(RedisHeaders.StreamMessageId);

        // Consume via XGROUP
        var epRead = CreateEndpoint($"XGROUP:{stream}",
            $"consumerGroup={group}&streamStartPosition=0&streamAutoAck=true");

        var received = new ConcurrentBag<object?>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                received.Add(ex.In.Body);
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RedisConsumer)epRead.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().NotBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // List consumer (BLPOP/BRPOP)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ListConsumer_BlpopReceivesMessage()
    {
        var key = $"test-blpop:{Guid.NewGuid():N}";

        var epCons = CreateEndpoint($"BLPOP:{key}", "pollDelayMs=200");
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RedisConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(500);

        var db = await GetDatabase();
        await db.ListLeftPushAsync(key, "blpop-test");

        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().Contain("blpop-test");
    }

    // ═══════════════════════════════════════════════════════════
    // Transactions
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Transacted_Set_CommitWrites()
    {
        var key = $"test-tx:{Guid.NewGuid():N}";

        var ep = CreateEndpoint($"SET:{key}", "transacted=true");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("transacted-value"));
        await producer.Process(exchange);

        // Value should NOT be set yet
        var db = await GetDatabase();
        (await db.KeyExistsAsync(key)).Should().BeFalse();

        // Commit
        exchange.Properties.Should().ContainKey("TRANSACT_ACTION");
        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        actions.Should().NotBeNull();
        foreach (var action in actions!.Values)
            await action.Commit();

        await producer.Stop();

        // Now value should exist
        (await db.StringGetAsync(key)).ToString().Should().Be("transacted-value");
    }

    [Fact]
    public async Task Transacted_Set_RollbackDoesNotWrite()
    {
        var key = $"test-rollback:{Guid.NewGuid():N}";

        var ep = CreateEndpoint($"SET:{key}", "transacted=true");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("should-not-persist"));
        await producer.Process(exchange);

        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        foreach (var action in actions!.Values)
            await action.Rollback();

        await producer.Stop();

        var db = await GetDatabase();
        (await db.KeyExistsAsync(key)).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // Key/Value — EXPIRE
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Expire_SetsKeyTtl()
    {
        var key = $"test-expire:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.StringSetAsync(key, "will-expire");

        var ep = CreateEndpoint($"EXPIRE:{key}", "ttl=2");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        exchange.Out!.Body.Should().Be(true);
        var ttl = await db.KeyTimeToLiveAsync(key);
        ttl.Should().NotBeNull();
        ttl!.Value.TotalSeconds.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(2);
    }

    // ═══════════════════════════════════════════════════════════
    // Pub/Sub — PSUBSCRIBE (pattern subscription)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PubSub_PSubscribe_ReceivesPatternMatch()
    {
        var prefix = $"ptest-{Guid.NewGuid():N}";
        var pattern = $"{prefix}.*";
        var actualChannel = $"{prefix}.events";
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var epSub = CreateEndpoint($"PSUBSCRIBE:{pattern}", "usePattern=true");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                received.Add(ex.In.Body?.ToString() ?? "");
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RedisConsumer)epSub.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(500);

        var epPub = CreateEndpoint($"PUBLISH:{actualChannel}");
        var pubProd = (RedisProducer)epPub.CreateProducer();
        await pubProd.Start();
        await pubProd.Process(new Exchange(new Message("pattern-test")));
        await pubProd.Stop();

        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().Contain("pattern-test");
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ═══════════════════════════════════════════════════════════
    // Streams — XREAD (without consumer group)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Stream_XRead_WithoutConsumerGroup()
    {
        var stream = $"test-xread-{Guid.NewGuid():N}";

        // First, add a message
        var epAdd = CreateEndpoint($"XADD:{stream}");
        var addProd = (RedisProducer)epAdd.CreateProducer();
        await addProd.Start();
        await addProd.Process(new Exchange(new Message("xread-data")));
        await addProd.Stop();

        // Now consume via XREAD (no group)
        var epRead = CreateEndpoint($"XREAD:{stream}", "streamStartPosition=0&streamBlockTimeMs=500");

        var received = new ConcurrentBag<object?>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                received.Add(ex.In.Body);
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RedisConsumer)epRead.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().NotBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // Streams — Manual Ack (StreamAutoAck=false with transacted)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Stream_ManualAck_AcksOnCommit()
    {
        var stream = $"test-ack-{Guid.NewGuid():N}";
        var group = $"grp-{Guid.NewGuid():N}";

        // Add a message
        var epAdd = CreateEndpoint($"XADD:{stream}");
        var addProd = (RedisProducer)epAdd.CreateProducer();
        await addProd.Start();
        await addProd.Process(new Exchange(new Message("ack-test")));
        await addProd.Stop();

        // XGROUP consumer with autoAck=true (non-transacted) → should auto-ack
        var epRead = CreateEndpoint($"XGROUP:{stream}",
            $"consumerGroup={group}&streamStartPosition=0&streamAutoAck=true");

        var receivedExchange = default(IExchange);
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                receivedExchange = callInfo.Arg<IExchange>();
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RedisConsumer)epRead.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        receivedExchange.Should().NotBeNull();
        receivedExchange!.In.Headers.Should().ContainKey(RedisHeaders.MessageId);

        // Verify auto-ack: pending list should be empty after autoAck
        var db = await GetDatabase();
        var pending = await db.StreamPendingAsync(stream, group);
        pending.PendingMessageCount.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // Lists — RPUSH, LPOP, LRANGE, BRPOP
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task List_Rpush_PushesToRight()
    {
        var key = $"test-rpush:{Guid.NewGuid():N}";

        var epPush = CreateEndpoint($"RPUSH:{key}");
        var pushProd = (RedisProducer)epPush.CreateProducer();
        await pushProd.Start();
        foreach (var item in new[] { "a", "b", "c" })
            await pushProd.Process(new Exchange(new Message(item)));
        await pushProd.Stop();

        var db = await GetDatabase();
        var list = await db.ListRangeAsync(key);
        list.Select(v => v.ToString()).Should().BeEquivalentTo(new[] { "a", "b", "c" },
            opt => opt.WithStrictOrdering());
    }

    [Fact]
    public async Task List_Lpop_PopsFromLeft()
    {
        var key = $"test-lpop:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.ListRightPushAsync(key, new RedisValue[] { "a", "b", "c" });

        var ep = CreateEndpoint($"LPOP:{key}");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message());
        await prod.Process(ex);
        await prod.Stop();

        ex.Out!.Body?.ToString().Should().Be("a");
    }

    [Fact]
    public async Task List_Lrange_ReturnsRange()
    {
        var key = $"test-lrange:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.ListRightPushAsync(key, new RedisValue[] { "a", "b", "c", "d", "e" });

        var ep = CreateEndpoint($"LRANGE:{key}", "start=1&stop=3");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message());
        await prod.Process(ex);
        await prod.Stop();

        var result = ex.Out!.Body as string[];
        result.Should().NotBeNull();
        result!.Should().BeEquivalentTo(new[] { "b", "c", "d" }, opt => opt.WithStrictOrdering());
    }

    [Fact]
    public async Task ListConsumer_BrpopReceivesMessage()
    {
        var key = $"test-brpop:{Guid.NewGuid():N}";

        var epCons = CreateEndpoint($"BRPOP:{key}", "pollDelayMs=200");
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RedisConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(500);

        var db = await GetDatabase();
        await db.ListRightPushAsync(key, "brpop-test");

        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().Contain("brpop-test");
    }

    // ═══════════════════════════════════════════════════════════
    // Hashes — HMSET, HMGET, HDEL, HLEN
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Hash_HmsetAndHmget()
    {
        var key = $"test-hm:{Guid.NewGuid():N}";

        // HMSET
        var epSet = CreateEndpoint($"HMSET:{key}");
        var setProd = (RedisProducer)epSet.CreateProducer();
        await setProd.Start();
        var setEx = new Exchange(new Message("ignored"));
        setEx.In.Headers[RedisHeaders.HashFields] = new Dictionary<string, object>
        {
            ["name"] = "Alice",
            ["age"] = "30",
            ["city"] = "Moscow"
        };
        await setProd.Process(setEx);
        await setProd.Stop();

        setEx.Out!.Body?.ToString().Should().Be("OK");

        // HMGET
        var epGet = CreateEndpoint($"HMGET:{key}");
        var getProd = (RedisProducer)epGet.CreateProducer();
        await getProd.Start();
        var getEx = new Exchange(new Message());
        getEx.In.Headers[RedisHeaders.FieldNames] = new string[] { "name", "city" };
        await getProd.Process(getEx);
        await getProd.Stop();

        var results = getEx.Out!.Body as string?[];
        results.Should().NotBeNull();
        results!.Should().BeEquivalentTo(new[] { "Alice", "Moscow" }, opt => opt.WithStrictOrdering());
    }

    [Fact]
    public async Task Hash_Hdel_RemovesField()
    {
        var key = $"test-hdel:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.HashSetAsync(key, new HashEntry[] { new("a", "1"), new("b", "2") });

        var ep = CreateEndpoint($"HDEL:{key}", "field=a");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message());
        await prod.Process(ex);
        await prod.Stop();

        ex.Out!.Body.Should().Be(true);
        (await db.HashExistsAsync(key, "a")).Should().BeFalse();
        (await db.HashExistsAsync(key, "b")).Should().BeTrue();
    }

    [Fact]
    public async Task Hash_Hlen_ReturnsCount()
    {
        var key = $"test-hlen:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.HashSetAsync(key, new HashEntry[] { new("x", "1"), new("y", "2"), new("z", "3") });

        var ep = CreateEndpoint($"HLEN:{key}");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message());
        await prod.Process(ex);
        await prod.Stop();

        ((long)ex.Out!.Body!).Should().Be(3);
    }

    // ═══════════════════════════════════════════════════════════
    // Sets — SREM, SISMEMBER
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_Srem_RemovesMember()
    {
        var key = $"test-srem:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.SetAddAsync(key, new RedisValue[] { "a", "b", "c" });

        var ep = CreateEndpoint($"SREM:{key}");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message("b"));
        await prod.Process(ex);
        await prod.Stop();

        ex.Out!.Body.Should().Be(true);
        var members = await db.SetMembersAsync(key);
        members.Select(m => m.ToString()).Should().BeEquivalentTo(new[] { "a", "c" });
    }

    [Fact]
    public async Task Set_Sismember_ChecksMembership()
    {
        var key = $"test-sism:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.SetAddAsync(key, new RedisValue[] { "x", "y" });

        var epYes = CreateEndpoint($"SISMEMBER:{key}");
        var prodYes = (RedisProducer)epYes.CreateProducer();
        await prodYes.Start();
        var exYes = new Exchange(new Message("x"));
        await prodYes.Process(exYes);
        await prodYes.Stop();
        exYes.Out!.Body.Should().Be(true);

        var epNo = CreateEndpoint($"SISMEMBER:{key}");
        var prodNo = (RedisProducer)epNo.CreateProducer();
        await prodNo.Start();
        var exNo = new Exchange(new Message("z"));
        await prodNo.Process(exNo);
        await prodNo.Stop();
        exNo.Out!.Body.Should().Be(false);
    }

    // ═══════════════════════════════════════════════════════════
    // Sorted Sets — ZREM, ZCARD, ZSCORE, ZRANGEBYSCORE
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task SortedSet_Zrem_RemovesMember()
    {
        var key = $"test-zrem:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.SortedSetAddAsync(key, new SortedSetEntry[] { new("a", 1), new("b", 2), new("c", 3) });

        var ep = CreateEndpoint($"ZREM:{key}");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message("b"));
        await prod.Process(ex);
        await prod.Stop();

        ex.Out!.Body.Should().Be(true);
        (await db.SortedSetLengthAsync(key)).Should().Be(2);
    }

    [Fact]
    public async Task SortedSet_Zcard_ReturnsCount()
    {
        var key = $"test-zcard:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.SortedSetAddAsync(key, new SortedSetEntry[] { new("a", 1), new("b", 2) });

        var ep = CreateEndpoint($"ZCARD:{key}");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message());
        await prod.Process(ex);
        await prod.Stop();

        ((long)ex.Out!.Body!).Should().Be(2);
    }

    [Fact]
    public async Task SortedSet_Zscore_ReturnsScore()
    {
        var key = $"test-zscore:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.SortedSetAddAsync(key, "alice", 99.5);

        var ep = CreateEndpoint($"ZSCORE:{key}");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message("alice"));
        await prod.Process(ex);
        await prod.Stop();

        ((double?)ex.Out!.Body).Should().Be(99.5);
    }

    [Fact]
    public async Task SortedSet_ZrangeByScore_ReturnsInScoreRange()
    {
        var key = $"test-zbyscore:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.SortedSetAddAsync(key, new SortedSetEntry[]
        {
            new("a", 10), new("b", 20), new("c", 30), new("d", 40), new("e", 50)
        });

        var ep = CreateEndpoint($"ZRANGEBYSCORE:{key}", "minScore=15&maxScore=45");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message());
        await prod.Process(ex);
        await prod.Stop();

        var result = ex.Out!.Body as string[];
        result.Should().NotBeNull();
        result!.Should().BeEquivalentTo(new[] { "b", "c", "d" }, opt => opt.WithStrictOrdering());
    }

    // ═══════════════════════════════════════════════════════════
    // Geospatial — GEOADD, GEODIST, GEORADIUS
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Geo_AddDistAndRadius()
    {
        var key = $"test-geo:{Guid.NewGuid():N}";

        // GEOADD — Moscow
        var epMoscow = CreateEndpoint($"GEOADD:{key}", "longitude=37.6173&latitude=55.7558");
        var moscowProd = (RedisProducer)epMoscow.CreateProducer();
        await moscowProd.Start();
        await moscowProd.Process(new Exchange(new Message("Moscow")));
        await moscowProd.Stop();

        // GEOADD — Saint Petersburg
        var epSpb = CreateEndpoint($"GEOADD:{key}", "longitude=30.3158&latitude=59.9343");
        var spbProd = (RedisProducer)epSpb.CreateProducer();
        await spbProd.Start();
        await spbProd.Process(new Exchange(new Message("SPb")));
        await spbProd.Stop();

        // GEODIST
        var epDist = CreateEndpoint($"GEODIST:{key}", "member1=Moscow&member2=SPb&geoUnit=km");
        var distProd = (RedisProducer)epDist.CreateProducer();
        await distProd.Start();
        var distEx = new Exchange(new Message());
        await distProd.Process(distEx);
        await distProd.Stop();

        var dist = (double?)distEx.Out!.Body;
        dist.Should().NotBeNull();
        dist!.Value.Should().BeGreaterThan(500).And.BeLessThan(800); // ~634 km

        // GEORADIUS — search around Moscow within 700 km
        var epRadius = CreateEndpoint($"GEORADIUS:{key}");
        var radiusProd = (RedisProducer)epRadius.CreateProducer();
        await radiusProd.Start();
        var radiusEx = new Exchange(new Message());
        radiusEx.In.Headers[RedisHeaders.CenterLongitude] = 37.6173;
        radiusEx.In.Headers[RedisHeaders.CenterLatitude] = 55.7558;
        radiusEx.In.Headers[RedisHeaders.Radius] = 700.0;
        radiusEx.In.Headers[RedisHeaders.RadiusUnit] = "km";
        await radiusProd.Process(radiusEx);
        await radiusProd.Stop();

        var found = radiusEx.Out!.Body as string[];
        found.Should().NotBeNull();
        found!.Should().Contain("Moscow");
        found.Should().Contain("SPb");
    }

    // ═══════════════════════════════════════════════════════════
    // HyperLogLog — PFADD, PFCOUNT, PFMERGE
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task HyperLogLog_AddAndCount()
    {
        var key = $"test-hll:{Guid.NewGuid():N}";

        var epAdd = CreateEndpoint($"PFADD:{key}");
        var addProd = (RedisProducer)epAdd.CreateProducer();
        await addProd.Start();
        foreach (var item in new[] { "a", "b", "c", "a", "b" })
            await addProd.Process(new Exchange(new Message(item)));
        await addProd.Stop();

        var epCount = CreateEndpoint($"PFCOUNT:{key}");
        var countProd = (RedisProducer)epCount.CreateProducer();
        await countProd.Start();
        var ex = new Exchange(new Message());
        await countProd.Process(ex);
        await countProd.Stop();

        ((long)ex.Out!.Body!).Should().Be(3);
    }

    [Fact]
    public async Task HyperLogLog_PfMerge_CombinesSets()
    {
        var key1 = $"test-hll1:{Guid.NewGuid():N}";
        var key2 = $"test-hll2:{Guid.NewGuid():N}";
        var dest = $"test-hll-dest:{Guid.NewGuid():N}";
        var db = await GetDatabase();

        await db.HyperLogLogAddAsync(key1, new RedisValue[] { "a", "b", "c" });
        await db.HyperLogLogAddAsync(key2, new RedisValue[] { "c", "d", "e" });

        var epMerge = CreateEndpoint($"PFMERGE:{dest}");
        var mergeProd = (RedisProducer)epMerge.CreateProducer();
        await mergeProd.Start();
        var mergeEx = new Exchange(new Message());
        mergeEx.In.Headers[RedisHeaders.SourceKeys] = new string[] { key1, key2 };
        await mergeProd.Process(mergeEx);
        await mergeProd.Stop();

        mergeEx.Out!.Body?.ToString().Should().Be("OK");

        var count = await db.HyperLogLogLengthAsync(dest);
        count.Should().Be(5); // {a, b, c, d, e}
    }

    // ═══════════════════════════════════════════════════════════
    // Bitmap — SETBIT, GETBIT, BITCOUNT
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Bitmap_SetBitAndGetBit()
    {
        var key = $"test-bit:{Guid.NewGuid():N}";

        var epSet = CreateEndpoint($"SETBIT:{key}", "offset=7&bit=true");
        var setProd = (RedisProducer)epSet.CreateProducer();
        await setProd.Start();
        await setProd.Process(new Exchange(new Message()));
        await setProd.Stop();

        var epGet = CreateEndpoint($"GETBIT:{key}", "offset=7");
        var getProd = (RedisProducer)epGet.CreateProducer();
        await getProd.Start();
        var getEx = new Exchange(new Message());
        await getProd.Process(getEx);
        await getProd.Stop();

        ((bool)getEx.Out!.Body!).Should().BeTrue();
    }

    [Fact]
    public async Task Bitmap_BitCount_CountsSetBits()
    {
        var key = $"test-bitcount:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.StringSetBitAsync(key, 0, true);
        await db.StringSetBitAsync(key, 3, true);
        await db.StringSetBitAsync(key, 7, true);

        var ep = CreateEndpoint($"BITCOUNT:{key}");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message());
        await prod.Process(ex);
        await prod.Stop();

        ((long)ex.Out!.Body!).Should().Be(3);
    }

    // ═══════════════════════════════════════════════════════════
    // Arbitrary Command
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Command_ExecutesArbitraryCommand()
    {
        var key = $"test-cmd:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.StringSetAsync(key, "hello-cmd");

        var ep = CreateEndpoint($"COMMAND:{key}", $"command=GET");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message(key));
        await prod.Process(ex);
        await prod.Stop();

        ex.Out!.Body?.ToString().Should().Be("hello-cmd");
    }

    [Fact]
    public async Task Command_WithArgs_ExecutesCustom()
    {
        var key = $"test-cmdargs:{Guid.NewGuid():N}";

        var ep = CreateEndpoint($"COMMAND:{key}", "command=SET");
        var prod = (RedisProducer)ep.CreateProducer();
        await prod.Start();
        var ex = new Exchange(new Message(key));
        ex.In.Headers[RedisHeaders.CommandArgs] = new string[] { key, "cmd-value" };
        await prod.Process(ex);
        await prod.Stop();

        var db = await GetDatabase();
        (await db.StringGetAsync(key)).ToString().Should().Be("cmd-value");
    }

    // ═══════════════════════════════════════════════════════════
    // Transactions — non-SET operations
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Transacted_Lpush_CommitWrites()
    {
        var key = $"test-tx-lpush:{Guid.NewGuid():N}";

        var ep = CreateEndpoint($"LPUSH:{key}", "transacted=true");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("tx-item"));
        await producer.Process(exchange);

        // Not committed yet
        var db = await GetDatabase();
        (await db.KeyExistsAsync(key)).Should().BeFalse();

        // Commit
        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        actions.Should().NotBeNull();
        foreach (var action in actions!.Values)
            await action.Commit();
        await producer.Stop();

        var items = await db.ListRangeAsync(key);
        items.Should().ContainSingle().Which.ToString().Should().Be("tx-item");
    }

    [Fact]
    public async Task Transacted_Sadd_CommitThenRollbackSecond()
    {
        var key = $"test-tx-sadd:{Guid.NewGuid():N}";
        var db = await GetDatabase();

        // First transacted SADD — commit
        var ep1 = CreateEndpoint($"SADD:{key}", "transacted=true");
        var prod1 = (RedisProducer)ep1.CreateProducer();
        await prod1.Start();
        var ex1 = new Exchange(new Message("item1"));
        await prod1.Process(ex1);
        var actions1 = ex1.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        foreach (var a in actions1!.Values) await a.Commit();
        await prod1.Stop();

        // Second transacted SADD — rollback
        var ep2 = CreateEndpoint($"SADD:{key}", "transacted=true");
        var prod2 = (RedisProducer)ep2.CreateProducer();
        await prod2.Start();
        var ex2 = new Exchange(new Message("item2"));
        await prod2.Process(ex2);
        var actions2 = ex2.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        foreach (var a in actions2!.Values) await a.Rollback();
        await prod2.Stop();

        var members = await db.SetMembersAsync(key);
        members.Select(m => m.ToString()).Should().BeEquivalentTo(new[] { "item1" });
    }

    [Fact]
    public async Task Transacted_Zadd_CommitWrites()
    {
        var key = $"test-tx-zadd:{Guid.NewGuid():N}";

        var ep = CreateEndpoint($"ZADD:{key}", "transacted=true&score=42");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("scored-item"));
        await producer.Process(exchange);

        var db = await GetDatabase();
        (await db.SortedSetLengthAsync(key)).Should().Be(0);

        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        foreach (var a in actions!.Values) await a.Commit();
        await producer.Stop();

        (await db.SortedSetScoreAsync(key, "scored-item")).Should().Be(42);
    }

    [Fact]
    public async Task Transacted_Hset_CommitWrites()
    {
        var key = $"test-tx-hset:{Guid.NewGuid():N}";

        var ep = CreateEndpoint($"HSET:{key}", "transacted=true&field=name");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("Alice"));
        await producer.Process(exchange);

        var db = await GetDatabase();
        (await db.HashExistsAsync(key, "name")).Should().BeFalse();

        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        foreach (var a in actions!.Values) await a.Commit();
        await producer.Stop();

        (await db.HashGetAsync(key, "name")).ToString().Should().Be("Alice");
    }

    [Fact]
    public async Task Transacted_Expire_CommitSetsExpiry()
    {
        var key = $"test-tx-expire:{Guid.NewGuid():N}";
        var db = await GetDatabase();
        await db.StringSetAsync(key, "persistent");

        var ep = CreateEndpoint($"EXPIRE:{key}", "transacted=true&ttl=5");
        var producer = (RedisProducer)ep.CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message());
        await producer.Process(exchange);

        // Not committed yet — key should have no TTL
        var ttlBefore = await db.KeyTimeToLiveAsync(key);
        ttlBefore.Should().BeNull();

        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        foreach (var a in actions!.Values) await a.Commit();
        await producer.Stop();

        var ttlAfter = await db.KeyTimeToLiveAsync(key);
        ttlAfter.Should().NotBeNull();
        ttlAfter!.Value.TotalSeconds.Should().BeGreaterThan(0);
    }

    // ═══════════════════════════════════════════════════════════
    // ConnectionFactory from Registry
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectionFactory_ResolvedFromRegistry()
    {
        // Create a RouteContext with a RedisConnectionFactory in the registry
        var context = new RouteContext();
        var factory = new RedisConnectionFactory
        {
            ConnectionString = ConnectionString
        };
        context.AddToRegistry("myRedisFactory", factory);

        // Create component and set context
        var component = new RedisComponent();
        context.AddComponent(component);

        // Create endpoint using connectionFactory parameter
        var key = $"test-factory:{Guid.NewGuid():N}";
        var uri = EndpointUriParser.Parse($"redis:SET:{key}?connectionFactory=myRedisFactory");
        var endpoint = (RedisEndpoint)component.CreateEndpoint(uri);

        var producer = (RedisProducer)endpoint.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("via-factory")));
        await producer.Stop();

        var db = await GetDatabase();
        (await db.StringGetAsync(key)).ToString().Should().Be("via-factory");
    }
}
