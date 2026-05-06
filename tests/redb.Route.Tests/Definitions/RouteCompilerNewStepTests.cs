using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Serialization;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests proving the RouteCompiler correctly handles Marshal, Unmarshal, Retry,
/// and DeadLetterChannel steps via the fluent DSL integrated through the route context.
/// </summary>
public class RouteCompiler_NewStepTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Marshal_SerializesBodyInRoute()
    {
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://marshal-in")
                .Marshal(typeof(JsonMessageSerializer))
                .To("direct://marshal-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://marshal-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://marshal-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = new { Name = "Test", Value = 42 } });
        await producer.Process(exchange);

        received.Should().NotBeNull();
        received!.In.Body.Should().BeOfType<byte[]>();
        received.In.Headers["Content-Type"].Should().Be("application/json");
    }

    [Fact]
    public async Task Unmarshal_DeserializesBodyInRoute()
    {
        IExchange? received = null;
        var serializer = new JsonMessageSerializer();
        var bytes = serializer.Serialize(new TestOrder("ORD-1", 99.9m));

        _context.AddRoutes(r =>
        {
            r.From("direct://unmarshal-in")
                .Unmarshal(typeof(JsonMessageSerializer), typeof(TestOrder))
                .To("direct://unmarshal-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://unmarshal-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://unmarshal-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = bytes });
        await producer.Process(exchange);

        received.Should().NotBeNull();
        received!.In.Body.Should().BeOfType<TestOrder>();
        var order = (TestOrder)received.In.Body!;
        order.Id.Should().Be("ORD-1");
        order.Amount.Should().Be(99.9m);
    }

    [Fact]
    public async Task MarshalThenUnmarshal_RoundtripInRoute()
    {
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://roundtrip-in")
                .Marshal(typeof(JsonMessageSerializer))
                .Unmarshal(typeof(JsonMessageSerializer), typeof(TestOrder))
                .To("direct://roundtrip-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://roundtrip-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://roundtrip-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = new TestOrder("ORD-2", 150m) });
        await producer.Process(exchange);

        received.Should().NotBeNull();
        received!.In.Body.Should().BeOfType<TestOrder>();
        var order = (TestOrder)received.In.Body!;
        order.Id.Should().Be("ORD-2");
        order.Amount.Should().Be(150m);
    }

    [Fact]
    public async Task Retry_RetriesOnFailure()
    {
        var callCount = 0;
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://retry-in")
                .Retry(3, TimeSpan.FromMilliseconds(1))
                .Process(ex =>
                {
                    callCount++;
                    if (callCount < 3)
                        throw new InvalidOperationException("transient");
                    received = ex;
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://retry-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "retry-me" });
        await producer.Process(exchange);

        callCount.Should().Be(3);
        received.Should().NotBeNull();
    }

    [Fact]
    public async Task DeadLetterChannel_RoutesFailedExchanges()
    {
        IExchange? deadLettered = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://dlc-in")
                .DeadLetterChannel("direct://dead-letter")
                .Process(async (IExchange ex, CancellationToken _) =>
                {
                    throw new InvalidOperationException("fatal");
                });
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://dead-letter")
                .Process(ex => deadLettered = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://dlc-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "doomed" });
        await producer.Process(exchange);

        deadLettered.Should().NotBeNull();
        deadLettered!.In.Headers.Should().ContainKey("CamelDeadLetterReason");
        deadLettered.ExceptionHandled.Should().BeTrue();
    }

    [Fact]
    public async Task RetryWithDeadLetter_RetriesThenDlc()
    {
        var callCount = 0;
        IExchange? deadLettered = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://retry-dlc-in")
                .Retry(2, TimeSpan.FromMilliseconds(1))
                .DeadLetterChannel("direct://retry-dlc-dead")
                .Process(async (IExchange _, CancellationToken _) =>
                {
                    callCount++;
                    throw new InvalidOperationException("always fails");
                });
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://retry-dlc-dead")
                .Process(ex => deadLettered = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://retry-dlc-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "data" });
        await producer.Process(exchange);

        // Should have retried 2 times + 1 original = 3 calls
        callCount.Should().Be(3);
        deadLettered.Should().NotBeNull("exchange should land in dead letter after retries exhausted");
    }

    public record TestOrder(string Id, decimal Amount);
}
