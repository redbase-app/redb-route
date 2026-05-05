using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Serialization;

namespace redb.Route.Tests.Serialization;

/// <summary>
/// Tests for <see cref="MarshalProcessor"/> and <see cref="UnmarshalProcessor"/>.
/// </summary>
public class MarshalUnmarshalProcessorTests
{
    private readonly JsonMessageSerializer _serializer = new();

    [Fact]
    public async Task MarshalProcessor_SerializesBodyToBytes()
    {
        var processor = new MarshalProcessor(_serializer);
        var exchange = new Exchange(new Message { Body = new OrderDto("ORD-1", 99.9m) });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeOfType<byte[]>();
        exchange.In.Headers.Should().ContainKey("Content-Type");
        exchange.In.Headers["Content-Type"].Should().Be("application/json");
    }

    [Fact]
    public async Task MarshalProcessor_SkipsIfBodyIsNull()
    {
        var processor = new MarshalProcessor(_serializer);
        var exchange = new Exchange(new Message { Body = null });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeNull();
    }

    [Fact]
    public async Task MarshalProcessor_SkipsIfBodyIsAlreadyBytes()
    {
        var processor = new MarshalProcessor(_serializer);
        var original = new byte[] { 1, 2, 3 };
        var exchange = new Exchange(new Message { Body = original });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeSameAs(original);
    }

    [Fact]
    public async Task UnmarshalProcessor_DeserializesFromBytes()
    {
        var original = new OrderDto("ORD-2", 42.5m);
        var bytes = _serializer.Serialize(original);

        var processor = new UnmarshalProcessor(_serializer, typeof(OrderDto));
        var exchange = new Exchange(new Message { Body = bytes });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeOfType<OrderDto>();
        var result = (OrderDto)exchange.In.Body!;
        result.Id.Should().Be("ORD-2");
        result.Amount.Should().Be(42.5m);
    }

    [Fact]
    public async Task UnmarshalProcessor_SkipsIfBodyIsNotBytes()
    {
        var processor = new UnmarshalProcessor(_serializer, typeof(OrderDto));
        var exchange = new Exchange(new Message { Body = "not bytes" });

        await processor.Process(exchange);

        exchange.In.Body.Should().Be("not bytes");
    }

    [Fact]
    public async Task MarshalThenUnmarshal_Roundtrip()
    {
        var marshal = new MarshalProcessor(_serializer);
        var unmarshal = new UnmarshalProcessor(_serializer, typeof(OrderDto));

        var original = new OrderDto("ORD-3", 100m);
        var exchange = new Exchange(new Message { Body = original });

        await marshal.Process(exchange);
        exchange.In.Body.Should().BeOfType<byte[]>();

        await unmarshal.Process(exchange);
        exchange.In.Body.Should().BeOfType<OrderDto>();

        var result = (OrderDto)exchange.In.Body!;
        result.Id.Should().Be("ORD-3");
        result.Amount.Should().Be(100m);
    }

    public record OrderDto(string Id, decimal Amount);

    // ── XML Serializer tests ──

    [Fact]
    public async Task MarshalProcessor_Xml_SerializesBodyToBytes()
    {
        var xmlSerializer = new XmlMessageSerializer();
        var processor = new MarshalProcessor(xmlSerializer);
        var exchange = new Exchange(new Message { Body = new XmlOrderDto { Id = "ORD-X1", Amount = 55m } });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeOfType<byte[]>();
        exchange.In.Headers["Content-Type"].Should().Be("application/xml");
    }

    [Fact]
    public async Task UnmarshalProcessor_Xml_DeserializesFromBytes()
    {
        var xmlSerializer = new XmlMessageSerializer();
        var original = new XmlOrderDto { Id = "ORD-X2", Amount = 77m };
        var bytes = xmlSerializer.Serialize(original);

        var processor = new UnmarshalProcessor(xmlSerializer, typeof(XmlOrderDto));
        var exchange = new Exchange(new Message { Body = bytes });

        await processor.Process(exchange);

        exchange.In.Body.Should().BeOfType<XmlOrderDto>();
        var result = (XmlOrderDto)exchange.In.Body!;
        result.Id.Should().Be("ORD-X2");
        result.Amount.Should().Be(77m);
    }

    [Fact]
    public async Task MarshalThenUnmarshal_Xml_Roundtrip()
    {
        var xmlSerializer = new XmlMessageSerializer();
        var marshal = new MarshalProcessor(xmlSerializer);
        var unmarshal = new UnmarshalProcessor(xmlSerializer, typeof(XmlOrderDto));

        var original = new XmlOrderDto { Id = "ORD-X3", Amount = 200m };
        var exchange = new Exchange(new Message { Body = original });

        await marshal.Process(exchange);
        exchange.In.Body.Should().BeOfType<byte[]>();

        await unmarshal.Process(exchange);
        exchange.In.Body.Should().BeOfType<XmlOrderDto>();

        var result = (XmlOrderDto)exchange.In.Body!;
        result.Id.Should().Be("ORD-X3");
        result.Amount.Should().Be(200m);
    }

    /// <summary>DTO for XML tests. Must be public with parameterless ctor.</summary>
    public class XmlOrderDto
    {
        public string? Id { get; set; }
        public decimal Amount { get; set; }
    }
}
