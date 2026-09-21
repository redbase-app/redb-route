using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Redis;
using redb.Route.Transactions;
using StackExchange.Redis;

namespace redb.Route.Tests.Redis;

/// <summary>
/// A deferred write (<c>transacted=true</c>) goes through the route's transaction the way a user writes it: inside
/// <c>.Transacted()</c>, committed by <see cref="TransactedProcessor"/> after the database, never by hand.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisTransactedWriteTests
{
    private const string ConnectionString = "localhost:6379";

    private static RedisEndpoint CreateEndpoint(string path, string? extraParams = null)
    {
        var qs = $"connectionString={ConnectionString}";
        if (extraParams is not null) qs += $"&{extraParams}";
        return (RedisEndpoint)new RedisComponent().CreateEndpoint(EndpointUriParser.Parse($"redis:{path}?{qs}"));
    }

    private static async Task<IDatabase> Db() => (await ConnectionMultiplexer.ConnectAsync(ConnectionString)).GetDatabase();

    /// <summary>Runs <paramref name="body"/> inside a transacted block, as <c>.Transacted()</c> on a route does.</summary>
    private static Task InTransaction(IExchange exchange, Func<IExchange, CancellationToken, Task> body) =>
        new TransactedProcessor(new DelegateProcessor(body), new TransactionPolicy()).Process(exchange);

    // ── 5. A deferred write outside a transacted block ──────────────

    [Fact]
    public async Task Transacted_write_outside_a_transacted_block_fails_instead_of_being_lost()
    {
        var key = $"tx-none:{Guid.NewGuid():N}";
        var producer = (RedisProducer)CreateEndpoint($"SET:{key}", "transacted=true").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message("value")));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Transacted()*");
        (await (await Db()).KeyExistsAsync(key)).Should().BeFalse();
        await producer.Stop();
    }

    [Fact]
    public async Task Transacted_write_after_the_transacted_block_ended_fails_instead_of_being_lost()
    {
        var key = $"tx-after:{Guid.NewGuid():N}";
        var producer = (RedisProducer)CreateEndpoint($"SET:{key}", "transacted=true").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("value"));

        await InTransaction(exchange, (_, _) => Task.CompletedTask);   // the block is over
        var act = () => producer.Process(exchange);                    // a transacted step after it

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Transacted()*");
        await producer.Stop();
    }

    [Fact]
    public async Task Transacted_write_inside_the_block_is_invisible_until_the_commit()
    {
        var key = $"tx-inside:{Guid.NewGuid():N}";
        var producer = (RedisProducer)CreateEndpoint($"SET:{key}", "transacted=true").CreateProducer();
        await producer.Start();
        var db = await Db();
        bool? existedInsideTheBlock = null;

        await InTransaction(new Exchange(new Message("value")), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            existedInsideTheBlock = await db.KeyExistsAsync(key);
        });

        existedInsideTheBlock.Should().BeFalse("the write is deferred until the transaction closed");
        (await db.StringGetAsync(key)).ToString().Should().Be("value");
        await producer.Stop();
    }

    // ── PUBLISH and XADD announce work: inside .Transacted() they wait for the commit ──

    private static async Task<RedisProducer> Started(string path, string? extraParams = null)
    {
        var producer = (RedisProducer)CreateEndpoint(path, extraParams).CreateProducer();
        await producer.Start();
        return producer;
    }

    private static async Task FailingBlock(IExchange exchange, RedisProducer producer)
    {
        var failed = () => InTransaction(exchange, async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            throw new InvalidOperationException("the unit of work failed");
        });
        await failed.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Subscribes to <paramref name="channel"/> and collects what is published during the test.</summary>
    private static async Task<(ConnectionMultiplexer Mux, List<string> Received)> Listen(string channel)
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        var received = new List<string>();
        await mux.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, value) =>
        {
            lock (received) received.Add(value.ToString());
        });
        return (mux, received);
    }

    [Fact]
    public async Task PUBLISH_without_transacted_inside_a_failed_block_publishes_nothing()
    {
        var channel = $"tx-pub-fail-{Guid.NewGuid():N}";
        var (mux, received) = await Listen(channel);
        var producer = await Started($"PUBLISH:{channel}");

        await FailingBlock(new Exchange(new Message("event")), producer);
        await Task.Delay(500);

        received.Should().BeEmpty("inside .Transacted() a publish waits for the commit unless transacted=false opts out");
        await producer.Stop();
        await mux.CloseAsync();
    }

    [Fact]
    public async Task PUBLISH_without_transacted_inside_a_block_goes_out_after_the_commit()
    {
        var channel = $"tx-pub-commit-{Guid.NewGuid():N}";
        var (mux, received) = await Listen(channel);
        var producer = await Started($"PUBLISH:{channel}");
        var exchange = new Exchange(new Message("event"));
        int? receivedInsideTheBlock = null;

        await InTransaction(exchange, async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            await Task.Delay(300, ct);
            lock (received) receivedInsideTheBlock = received.Count;
        });
        await Task.Delay(500);

        receivedInsideTheBlock.Should().Be(0, "nothing is announced before the database has committed");
        received.Should().Equal("event");
        exchange.In.Headers.Should().ContainKey(RedisHeaders.PublishRecipients, "the recipient count arrives with the commit");
        await producer.Stop();
        await mux.CloseAsync();
    }

    [Fact]
    public async Task PUBLISH_with_transacted_false_goes_out_at_once_even_from_a_failed_block()
    {
        var channel = $"tx-pub-optout-{Guid.NewGuid():N}";
        var (mux, received) = await Listen(channel);
        var producer = await Started($"PUBLISH:{channel}", "transacted=false");

        await FailingBlock(new Exchange(new Message("event")), producer);
        await Task.Delay(500);

        received.Should().Equal("event");
        await producer.Stop();
        await mux.CloseAsync();
    }

    [Fact]
    public async Task XADD_without_transacted_inside_a_failed_block_adds_nothing()
    {
        var stream = $"tx-xadd-fail:{Guid.NewGuid():N}";
        var producer = await Started($"XADD:{stream}");

        await FailingBlock(new Exchange(new Message("event")), producer);

        (await (await Db()).StreamLengthAsync(stream)).Should().Be(0,
            "inside .Transacted() an XADD waits for the commit unless transacted=false opts out");
        await producer.Stop();
    }

    [Fact]
    public async Task XADD_without_transacted_inside_a_block_adds_after_the_commit_and_reports_the_id()
    {
        var stream = $"tx-xadd-commit:{Guid.NewGuid():N}";
        var producer = await Started($"XADD:{stream}");
        var db = await Db();
        var exchange = new Exchange(new Message("event"));
        long? lengthInsideTheBlock = null;

        await InTransaction(exchange, async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            lengthInsideTheBlock = await db.StreamLengthAsync(stream);
        });

        lengthInsideTheBlock.Should().Be(0);
        var entries = await db.StreamRangeAsync(stream);
        entries.Should().ContainSingle();
        exchange.In.Headers[RedisHeaders.StreamMessageId].Should().Be(entries[0].Id.ToString(),
            "the entry id arrives with the commit");
        await producer.Stop();
    }

    // ── Data operations run at once unless transacted=true asks for the commit ──

    [Fact]
    public async Task SET_without_transacted_inside_a_failed_block_is_written_at_once()
    {
        var key = $"tx-set-data:{Guid.NewGuid():N}";
        var producer = await Started($"SET:{key}");

        await FailingBlock(new Exchange(new Message("value")), producer);

        (await (await Db()).StringGetAsync(key)).ToString().Should().Be("value",
            "a data operation is not an announcement: it waits for the commit only with transacted=true");
        await producer.Stop();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("EXISTS")]
    [InlineData("LPOP")]
    [InlineData("HGETALL")]
    public void Operation_that_exists_for_its_result_refuses_transacted_true(string operation)
    {
        var endpoint = CreateEndpoint($"{operation}:tx-refuse-{Guid.NewGuid():N}", "transacted=true");

        var act = () => endpoint.CreateProducer();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{operation}*transacted*");
    }

    // ── A deferred write is the same write, done later (item 4) ──

    [Fact]
    public async Task Deferred_SET_keeps_its_ttl()
    {
        var key = $"tx-set-ttl:{Guid.NewGuid():N}";
        var producer = await Started($"SET:{key}", "transacted=true&ttl=120");

        await InTransaction(new Exchange(new Message("value")), (ex, ct) => producer.Process(ex, ct));

        var ttl = await (await Db()).KeyTimeToLiveAsync(key);
        ttl.Should().NotBeNull("SET with ttl=120 must expire").And.Subject!.Value.TotalSeconds.Should().BeInRange(100, 120);
        await producer.Stop();
    }

    [Fact]
    public async Task Deferred_PFADD_is_carried_out_not_dropped()
    {
        var key = $"tx-pfadd:{Guid.NewGuid():N}";
        var producer = await Started($"PFADD:{key}", "transacted=true");

        await InTransaction(new Exchange(new Message("visitor-1")), (ex, ct) => producer.Process(ex, ct));

        (await (await Db()).HyperLogLogLengthAsync(key)).Should().Be(1);
        await producer.Stop();
    }

    [Fact]
    public async Task Deferred_SET_writes_a_byte_array_body_as_its_bytes()
    {
        var key = $"tx-set-bin:{Guid.NewGuid():N}";
        byte[] binary = [0x00, 0xFF, 0x80, 0x7B, 0x22, 0xC3];
        var producer = await Started($"SET:{key}", "transacted=true");

        await InTransaction(new Exchange(new Message(binary)), (ex, ct) => producer.Process(ex, ct));

        ((byte[]?)await (await Db()).StringGetAsync(key)).Should().Equal(binary);
        await producer.Stop();
    }
}
