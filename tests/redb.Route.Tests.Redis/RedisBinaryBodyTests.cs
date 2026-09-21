using redb.Route.Core;
using redb.Route.Redis;
using StackExchange.Redis;

namespace redb.Route.Tests.Redis;

/// <summary>
/// A <c>byte[]</c> body (what <c>.Marshal()</c> leaves behind) reaches Redis as its bytes on every write, not as the
/// text <c>"System.Byte[]"</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisBinaryBodyTests
{
    private const string ConnectionString = "localhost:6379";

    // Not valid UTF-8: survives only if nobody turns it into text on the way.
    private static readonly byte[] Binary = [0x00, 0xFF, 0x80, 0x7B, 0x22, 0xC3];

    private static RedisProducer Producer(string path) =>
        (RedisProducer)new RedisComponent()
            .CreateEndpoint(EndpointUriParser.Parse($"redis:{path}?connectionString={ConnectionString}"))
            .CreateProducer();

    [Fact]
    public async Task SET_writes_a_byte_array_body_as_its_bytes()
    {
        var key = $"bin-set:{Guid.NewGuid():N}";
        var producer = Producer($"SET:{key}");
        await producer.Start();

        await producer.Process(new Exchange(new Message(Binary)));

        var db = (await ConnectionMultiplexer.ConnectAsync(ConnectionString)).GetDatabase();
        ((byte[]?)await db.StringGetAsync(key)).Should().Equal(Binary);
        await db.KeyDeleteAsync(key);
        await producer.Stop();
    }

    [Fact]
    public async Task SETNX_writes_a_byte_array_body_as_its_bytes()
    {
        var key = $"bin-setnx:{Guid.NewGuid():N}";
        var producer = Producer($"SETNX:{key}");
        await producer.Start();

        await producer.Process(new Exchange(new Message(Binary)));

        var db = (await ConnectionMultiplexer.ConnectAsync(ConnectionString)).GetDatabase();
        ((byte[]?)await db.StringGetAsync(key)).Should().Equal(Binary);
        await db.KeyDeleteAsync(key);
        await producer.Stop();
    }

    [Fact]
    public async Task PUBLISH_sends_a_byte_array_body_as_its_bytes()
    {
        var channel = $"bin-pub-{Guid.NewGuid():N}";
        var mux = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await mux.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, value) => received.TrySetResult((byte[])value!));

        var producer = Producer($"PUBLISH:{channel}");
        await producer.Start();
        await producer.Process(new Exchange(new Message(Binary)));

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Equal(Binary);
        await producer.Stop();
        await mux.CloseAsync();
    }
}
