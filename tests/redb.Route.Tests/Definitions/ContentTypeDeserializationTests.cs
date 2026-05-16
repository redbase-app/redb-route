using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Serialization;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Integration tests for ContentType-based deserialization via
/// <c>Unmarshal&lt;T&gt;()</c> and <c>OfType&lt;T&gt;()</c> auto-conversion.
/// </summary>
public class ContentTypeDeserializationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();
    private readonly JsonMessageSerializer _json = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UnmarshalGeneric_DeserializesByContentType()
    {
        IExchange? received = null;
        var bytes = _json.Serialize(new OrderDto("ORD-1", 99m));

        _context.AddRoutes(r =>
        {
            r.From("direct://unmarshal-auto-in")
                .Unmarshal<OrderDto>()
                .Process(ex => received = ex);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://unmarshal-auto-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message
        {
            Body = bytes,
            ContentType = "application/json"
        }));

        received.Should().NotBeNull();
        var order = received!.In.Body.Should().BeOfType<OrderDto>().Subject;
        order.Id.Should().Be("ORD-1");
        order.Amount.Should().Be(99m);
    }

    [Fact]
    public async Task UnmarshalGeneric_FromString_DeserializesByContentType()
    {
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://unmarshal-str-in")
                .Unmarshal<OrderDto>()
                .Process(ex => received = ex);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://unmarshal-str-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message
        {
            Body = """{"id":"ORD-2","amount":50}""",
            ContentType = "application/json"
        }));

        received.Should().NotBeNull();
        var order = received!.In.Body.Should().BeOfType<OrderDto>().Subject;
        order.Id.Should().Be("ORD-2");
    }

    [Fact]
    public async Task OfType_AutoConvertsJsonBody()
    {
        OrderDto? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://oftype-json-in")
                .OfType<OrderDto>()
                .Process(order =>
                {
                    received = order;
                });
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://oftype-json-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message
        {
            Body = """{"id":"ORD-3","amount":200}""",
            ContentType = "application/json"
        }));

        received.Should().NotBeNull();
        received!.Id.Should().Be("ORD-3");
        received.Amount.Should().Be(200m);
    }

    [Fact]
    public async Task OfType_ExistingBody_PassesThrough()
    {
        OrderDto? received = null;
        var original = new OrderDto("ORD-4", 10m);

        _context.AddRoutes(r =>
        {
            r.From("direct://oftype-passthrough")
                .OfType<OrderDto>()
                .Process(order => received = order);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://oftype-passthrough").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = original }));

        received.Should().BeSameAs(original);
    }

    [Fact]
    public async Task OfType_WithFilter_FiltersDeserialized()
    {
        var received = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://oftype-filter-in")
                .OfType<OrderDto>()
                .Filter(o => o.Amount > 100)
                .Process(order => received.Add(order.Id));
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://oftype-filter-in").CreateProducer();
        await producer.Start();

        // passes filter
        await producer.Process(new Exchange(new Message
        {
            Body = """{"id":"BIG","amount":500}""",
            ContentType = "application/json"
        }));

        // filtered out
        await producer.Process(new Exchange(new Message
        {
            Body = """{"id":"SMALL","amount":10}""",
            ContentType = "application/json"
        }));

        received.Should().ContainSingle().Which.Should().Be("BIG");
    }

    [Fact]
    public async Task OfType_WithTransform_ChangesType()
    {
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://oftype-transform-in")
                .OfType<OrderDto>()
                .Transform(o => $"Order {o.Id} total: {o.Amount}")
                .To("direct://oftype-transform-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://oftype-transform-out")
                .Process(ex => received = ex);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://oftype-transform-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message
        {
            Body = """{"id":"X","amount":42}""",
            ContentType = "application/json"
        }));

        received.Should().NotBeNull();
        received!.In.Body.Should().Be("Order X total: 42");
    }

    [Fact]
    public async Task OfType_String_SkipsConvertBody()
    {
        string? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://oftype-string-in")
                .OfType<string>()
                .Process(s => received = s);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://oftype-string-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "hello" }));

        received.Should().Be("hello");
    }

    [Fact]
    public async Task OfType_XmlContentType_DeserializesXml()
    {
        XmlOrder? received = null;
        var xml = new XmlMessageSerializer();
        var bytes = xml.Serialize(new XmlOrder { Id = "XML-1", Total = 77m });

        _context.AddRoutes(r =>
        {
            r.From("direct://oftype-xml")
                .OfType<XmlOrder>()
                .Process(order => received = order);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://oftype-xml").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message
        {
            Body = bytes,
            ContentType = "application/xml"
        }));

        received.Should().NotBeNull();
        received!.Id.Should().Be("XML-1");
        received.Total.Should().Be(77m);
    }

    public record OrderDto(string Id, decimal Amount);

    public class XmlOrder
    {
        public string Id { get; set; } = "";
        public decimal Total { get; set; }
    }
}
