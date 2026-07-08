using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests;

/// <summary>
/// End-to-end tests proving the route engine actually works with DSL + compilation + direct component.
/// </summary>
public class RouteContextTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Direct_passthrough_route_delivers_exchange()
    {
        // Arrange: direct://input → direct://output
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://input")
                .To("direct://output");
        });

        // Add a second route that captures output
        _context.AddRoutes(r =>
        {
            r.From("direct://output")
                .Process(exchange => received = exchange);
        });

        await _context.Start();

        // Act: send a message to direct://input
        var inputEndpoint = _context.GetEndpoint("direct://input");
        var producer = inputEndpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "Hello World" });
        await producer.Process(exchange);

        // Assert
        received.Should().NotBeNull();
        received!.In.Body.Should().Be("Hello World");
    }

    [Fact]
    public async Task Route_with_processor_transforms_exchange()
    {
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://input")
                .Process(e => e.In.Body = $"Processed: {e.In.Body}")
                .To("direct://output");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://output")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://input").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "test" }));

        received.Should().NotBeNull();
        received!.In.Body.Should().Be("Processed: test");
    }

    [Fact]
    public async Task Route_with_set_header_and_set_body()
    {
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://start")
                .SetHeader("X-Processed", true)
                .SetBody("replaced-body")
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://start").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "original" }));

        received.Should().NotBeNull();
        received!.In.Body.Should().Be("replaced-body");
        received.In.Headers["X-Processed"].Should().Be(true);
    }

    [Fact]
    public async Task Route_with_filter_stops_non_matching()
    {
        var received = new List<object?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://filtered")
                .Filter(e => (int)e.In.Body! > 5)
                .Process(e => received.Add(e.In.Body));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://filtered").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = 3 }));
        await producer.Process(new Exchange(new Message { Body = 10 }));
        await producer.Process(new Exchange(new Message { Body = 1 }));
        await producer.Process(new Exchange(new Message { Body = 7 }));

        received.Should().HaveCount(2);
        received.Should().Contain(10);
        received.Should().Contain(7);
    }

    [Fact]
    public async Task Route_with_choice_routes_by_content()
    {
        var highPriority = new List<string>();
        var lowPriority = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://orders")
                .Choice()
                    .When(e => (string)e.In.Headers["priority"]! == "high")
                        .Process(e => highPriority.Add((string)e.In.Body!))
                    .When(e => (string)e.In.Headers["priority"]! == "low")
                        .Process(e => lowPriority.Add((string)e.In.Body!))
                .End();
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://orders").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message
        {
            Body = "order1",
            Headers = { ["priority"] = "high" }
        }));

        await producer.Process(new Exchange(new Message
        {
            Body = "order2",
            Headers = { ["priority"] = "low" }
        }));

        await producer.Process(new Exchange(new Message
        {
            Body = "order3",
            Headers = { ["priority"] = "high" }
        }));

        highPriority.Should().BeEquivalentTo("order1", "order3");
        lowPriority.Should().BeEquivalentTo("order2");
    }

    [Fact]
    public async Task Route_with_transform()
    {
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://transform")
                .Transform(e => ((string)e.In.Body!).ToUpperInvariant())
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://transform").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "hello" }));

        received.Should().NotBeNull();
        received!.In.Body.Should().Be("HELLO");
    }

    [Fact]
    public async Task Route_with_try_catch_handles_exception()
    {
        IExchange? received = null;
        Exception? caughtException = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://errors")
                .DoTry()
                    .Process(e => throw new InvalidOperationException("boom"))
                .DoCatch<InvalidOperationException>()
                    .Process(e =>
                    {
                        caughtException = e.Exception;
                        e.In.Body = "recovered";
                    })
                .End()
                .Process(e => received = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://errors").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "test" }));

        caughtException.Should().NotBeNull();
        caughtException!.Message.Should().Be("boom");
        received.Should().NotBeNull();
        received!.In.Body.Should().Be("recovered");
    }

    [Fact]
    public async Task Route_with_multi_step_pipeline()
    {
        var steps = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://pipeline")
                .Process(e => steps.Add("step1"))
                .SetHeader("step", "2")
                .Process(e => steps.Add($"step{e.In.Headers["step"]}"))
                .SetBody((Func<IExchange, object?>)(e => $"final-{e.In.Body}"))
                .Process(e => steps.Add($"step3-{e.In.Body}"));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://pipeline").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "data" }));

        steps.Should().BeEquivalentTo("step1", "step2", "step3-final-data");
    }

    [Fact]
    public async Task Route_assigns_route_id()
    {
        string? capturedRouteId = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://identified")
                .RouteId("my-route")
                .Process(e => capturedRouteId = e.RouteId);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://identified").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "test" }));

        capturedRouteId.Should().Be("my-route");
    }

    [Fact]
    public async Task Route_with_delay()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool processed = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://delayed")
                .Delay(TimeSpan.FromMilliseconds(100))
                .Process(e => processed = true);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://delayed").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "test" }));

        sw.Stop();
        processed.Should().BeTrue();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(80); // account for scheduling jitter
    }

    [Fact]
    public async Task Route_with_stop_halts_pipeline()
    {
        var steps = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://halted")
                .Process(e => steps.Add("before"))
                .Stop()
                .Process(e => steps.Add("after")); // should NOT execute
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://halted").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "test" }));

        steps.Should().BeEquivalentTo("before");
    }

    [Fact]
    public async Task Multiple_routes_in_one_builder()
    {
        var results = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://a")
                .Process(e => results.Add("route-a"));

            r.From("direct://b")
                .Process(e => results.Add("route-b"));
        });

        await _context.Start();

        var producerA = _context.GetEndpoint("direct://a").CreateProducer();
        await producerA.Start();
        await producerA.Process(new Exchange(new Message()));

        var producerB = _context.GetEndpoint("direct://b").CreateProducer();
        await producerB.Start();
        await producerB.Process(new Exchange(new Message()));

        results.Should().BeEquivalentTo("route-a", "route-b");
    }

    [Fact]
    public async Task Engine_reports_compiled_routes()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://one").RouteId("r1").Process(_ => { });
            r.From("direct://two").RouteId("r2").Process(_ => { });
        });

        await _context.Start();

        _context.Routes.Should().HaveCount(2);
        _context.Routes.Select(r => r.RouteId).Should().BeEquivalentTo("r1", "r2");
    }

    [Fact]
    public async Task Route_set_property_is_accessible_in_later_step()
    {
        object? capturedProp = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://props")
                .SetProperty("myKey", "myValue")
                .Process(e => capturedProp = e.Properties["myKey"]);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://props").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message()));

        capturedProp.Should().Be("myValue");
    }

    [Fact]
    public async Task Route_with_remove_header()
    {
        bool headerExists = true;

        _context.AddRoutes(r =>
        {
            r.From("direct://remove-hdr")
                .RemoveHeader("toRemove")
                .Process(e => headerExists = e.In.Headers.ContainsKey("toRemove"));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://remove-hdr").CreateProducer();
        await producer.Start();

        var msg = new Message { Body = "test" };
        msg.Headers["toRemove"] = "value";
        await producer.Process(new Exchange(msg));

        headerExists.Should().BeFalse();
    }
}
