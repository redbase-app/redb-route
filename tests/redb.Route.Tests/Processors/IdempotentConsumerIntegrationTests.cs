using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for IdempotentConsumer within route definitions.
/// </summary>
public class IdempotentConsumerIntegrationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task IdempotentConsumer_InRoute_SkipsDuplicate()
    {
        var received = new List<object?>();
        var repo = new InMemoryIdempotentRepository();

        _context.AddRoutes(r =>
        {
            r.From("direct://dedup")
                .IdempotentConsumer(e => e.In.Headers["MessageId"]?.ToString()!, repo)
                .Process(e => received.Add(e.In.Body));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://dedup").CreateProducer();
        await producer.Start();

        var msg1 = new Message { Body = "first" };
        msg1.Headers["MessageId"] = "id-1";
        await producer.Process(new Exchange(msg1));

        var msg2 = new Message { Body = "duplicate" };
        msg2.Headers["MessageId"] = "id-1"; // same key
        await producer.Process(new Exchange(msg2));

        var msg3 = new Message { Body = "third" };
        msg3.Headers["MessageId"] = "id-2"; // different key
        await producer.Process(new Exchange(msg3));

        received.Should().HaveCount(2);
        received[0].Should().Be("first");
        received[1].Should().Be("third");
    }

    [Fact]
    public async Task IdempotentConsumer_WithSkipFalse_PropagatesDuplicate()
    {
        var received = new List<object?>();
        var duplicateFlags = new List<bool>();
        var repo = new InMemoryIdempotentRepository();

        _context.AddRoutes(r =>
        {
            r.From("direct://noskip")
                .IdempotentConsumer(
                    e => e.In.Headers["MessageId"]?.ToString()!,
                    repo,
                    skipDuplicate: false)
                .Process(e =>
                {
                    received.Add(e.In.Body);
                    duplicateFlags.Add(
                        e.Properties.TryGetValue("CamelDuplicateMessage", out var val) && val is true);
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://noskip").CreateProducer();
        await producer.Start();

        var msg1 = new Message { Body = "original" };
        msg1.Headers["MessageId"] = "ns-1";
        await producer.Process(new Exchange(msg1));

        var msg2 = new Message { Body = "duplicate" };
        msg2.Headers["MessageId"] = "ns-1";
        await producer.Process(new Exchange(msg2));

        received.Should().HaveCount(2);
        duplicateFlags[0].Should().BeFalse(); // first message is not duplicate
        duplicateFlags[1].Should().BeTrue();  // second message is duplicate
    }

    [Fact]
    public async Task IdempotentConsumer_InRoute_WithMockEndpoint()
    {
        var repo = new InMemoryIdempotentRepository();

        _context.AddRoutes(r =>
        {
            r.From("direct://mock-dedup")
                .IdempotentConsumer(e => e.In.Body?.ToString()!, repo)
                .To("mock://output");
        });

        await _context.Start();

        var mockEndpoint = (MockEndpoint)_context.GetEndpoint("mock://output");

        var producer = _context.GetEndpoint("direct://mock-dedup").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "unique-1" }));
        await producer.Process(new Exchange(new Message { Body = "unique-2" }));
        await producer.Process(new Exchange(new Message { Body = "unique-1" })); // duplicate
        await producer.Process(new Exchange(new Message { Body = "unique-3" }));

        mockEndpoint.ReceivedCount.Should().Be(3); // unique-1, unique-2, unique-3
    }
}
