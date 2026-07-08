using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Serialization;

namespace redb.Route.Tests.Serialization;

/// <summary>
/// Tests for <see cref="ConvertBodyProcessor"/> with <see cref="IDataFormatRegistry"/> support.
/// </summary>
public class ConvertBodyProcessorTests
{
    private readonly DataFormatRegistry _registry = new();

    [Fact]
    public async Task JsonContentType_DeserializesFromString()
    {
        var processor = new ConvertBodyProcessor(typeof(TestOrder), _registry);
        var exchange = new Exchange(new Message
        {
            Body = """{"id":"ORD-1","amount":42.5}""",
            ContentType = "application/json"
        });

        await processor.Process(exchange);

        var order = exchange.In.Body.Should().BeOfType<TestOrder>().Subject;
        order.Id.Should().Be("ORD-1");
        order.Amount.Should().Be(42.5m);
    }

    [Fact]
    public async Task JsonContentType_DeserializesFromBytes()
    {
        var json = new JsonMessageSerializer();
        var bytes = json.Serialize(new TestOrder { Id = "ORD-2", Amount = 100m });

        var processor = new ConvertBodyProcessor(typeof(TestOrder), _registry);
        var exchange = new Exchange(new Message
        {
            Body = bytes,
            ContentType = "application/json"
        });

        await processor.Process(exchange);

        var order = exchange.In.Body.Should().BeOfType<TestOrder>().Subject;
        order.Id.Should().Be("ORD-2");
        order.Amount.Should().Be(100m);
    }

    [Fact]
    public async Task JsonWithCharset_StillDeserializes()
    {
        var processor = new ConvertBodyProcessor(typeof(TestOrder), _registry);
        var exchange = new Exchange(new Message
        {
            Body = """{"id":"ORD-3","amount":0}""",
            ContentType = "application/json; charset=utf-8"
        });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeOfType<TestOrder>();
    }

    [Fact]
    public async Task StructuredSuffix_PlusJson_Deserializes()
    {
        var processor = new ConvertBodyProcessor(typeof(TestOrder), _registry);
        var exchange = new Exchange(new Message
        {
            Body = """{"id":"ORD-4","amount":1}""",
            ContentType = "application/vnd.company+json"
        });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeOfType<TestOrder>();
    }

    [Fact]
    public async Task NoRegistry_FallsBackToSystemConvert()
    {
        var processor = new ConvertBodyProcessor(typeof(string));
        var exchange = new Exchange(new Message
        {
            Body = new byte[] { 72, 101, 108, 108, 111 } // "Hello"
        });

        await processor.Process(exchange);

        exchange.In.Body.Should().Be("Hello");
    }

    [Fact]
    public async Task BodyAlreadyTargetType_NoOp()
    {
        var original = new TestOrder { Id = "X", Amount = 1 };
        var processor = new ConvertBodyProcessor(typeof(TestOrder), _registry);
        var exchange = new Exchange(new Message { Body = original });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeSameAs(original);
    }

    [Fact]
    public async Task UnknownContentType_NoRegistry_Throws()
    {
        var processor = new ConvertBodyProcessor(typeof(TestOrder));
        var exchange = new Exchange(new Message
        {
            Body = "not-convertible",
            ContentType = "application/octet-stream"
        });

        var act = () => processor.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task NullBody_NoOp()
    {
        var processor = new ConvertBodyProcessor(typeof(TestOrder), _registry);
        var exchange = new Exchange(new Message { Body = null });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeNull();
    }

    // ── Stream conversion tests ──

    [Fact]
    public async Task StreamToString_ReturnsDecodedString()
    {
        var data = "hello streaming world"u8.ToArray();
        var processor = new ConvertBodyProcessor(typeof(string));
        var exchange = new Exchange(new Message(new MemoryStream(data)));

        await processor.Process(exchange);

        exchange.In.Body.Should().Be("hello streaming world");
    }

    [Fact]
    public async Task StreamToByteArray_ReturnsCopiedBytes()
    {
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var processor = new ConvertBodyProcessor(typeof(byte[]));
        var exchange = new Exchange(new Message(new MemoryStream(data)));

        await processor.Process(exchange);

        exchange.In.Body.Should().BeOfType<byte[]>()
            .Which.Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
    }

    [Fact]
    public async Task ByteArrayToStream_ReturnsReadableStream()
    {
        var data = new byte[] { 1, 2, 3 };
        var processor = new ConvertBodyProcessor(typeof(Stream));
        var exchange = new Exchange(new Message(data));

        await processor.Process(exchange);

        var stream = exchange.In.Body.Should().BeAssignableTo<Stream>().Subject;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);
        ms.ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task StringToStream_ReturnsReadableStream()
    {
        var processor = new ConvertBodyProcessor(typeof(Stream));
        var exchange = new Exchange(new Message("test data"));

        await processor.Process(exchange);

        var stream = exchange.In.Body.Should().BeAssignableTo<Stream>().Subject;
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        text.Should().Be("test data");
    }

    [Fact]
    public async Task EmptyStreamToByteArray_ReturnsEmptyArray()
    {
        var processor = new ConvertBodyProcessor(typeof(byte[]));
        var exchange = new Exchange(new Message(new MemoryStream()));

        await processor.Process(exchange);

        exchange.In.Body.Should().BeOfType<byte[]>()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAlreadyStream_NoOp()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var processor = new ConvertBodyProcessor(typeof(Stream));
        var exchange = new Exchange(new Message(stream));

        await processor.Process(exchange);

        exchange.In.Body.Should().BeSameAs(stream);
    }

    public class TestOrder
    {
        public string Id { get; set; } = "";
        public decimal Amount { get; set; }
    }
}
