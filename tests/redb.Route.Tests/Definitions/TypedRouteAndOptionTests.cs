using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for typed route definitions (<see cref="redb.Route.Definitions.RouteDefinition{TIn}"/>).
/// </summary>
public class TypedRouteDefinitionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TypedRoute_FilterByType_OnlyProcessesMatchingMessages()
    {
        var received = new List<object?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://typed-in")
                .OfType<OrderMessage>()
                .Filter(order => order.Amount > 50)
                .Process(ex => received.Add(ex.In.Body));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://typed-in").CreateProducer();
        await producer.Start();

        // This one should pass the filter
        await producer.Process(new Exchange(new Message { Body = new OrderMessage("ORD-1", 100m) }));

        // This one should be filtered out (amount <= 50)
        await producer.Process(new Exchange(new Message { Body = new OrderMessage("ORD-2", 25m) }));

        received.Should().HaveCount(1);
        ((OrderMessage)received[0]!).Id.Should().Be("ORD-1");
    }

    [Fact]
    public async Task TypedRoute_Transform_ChangesBody()
    {
        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://transform-in")
                .OfType<OrderMessage>()
                .Transform(order => new OrderResult(order.Id, true))
                .To("direct://transform-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://transform-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://transform-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = new OrderMessage("ORD-3", 99m) }));

        received.Should().NotBeNull();
        received!.In.Body.Should().BeOfType<OrderResult>();
        var result = (OrderResult)received.In.Body!;
        result.OrderId.Should().Be("ORD-3");
        result.Accepted.Should().BeTrue();
    }

    public record OrderMessage(string Id, decimal Amount);
    public record OrderResult(string OrderId, bool Accepted);
}

/// <summary>
/// Tests for <see cref="RouteEngineOptions"/> integration.
/// </summary>
public class RouteEngineOptionsTests : IAsyncDisposable
{
    private RouteContext? _context;

    public async ValueTask DisposeAsync()
    {
        if (_context != null)
            await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Default_Options_AreReasonable()
    {
        var options = new RouteEngineOptions();

        options.EnableTelemetry.Should().BeTrue();
        options.EnableMetrics.Should().BeTrue();
        options.ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.ThrowOnCompilationError.Should().BeTrue();
        RouteEngineOptions.SectionName.Should().Be("RedbRoute");
    }

    [Fact]
    public async Task Engine_WithTelemetryDisabled_StillWorks()
    {
        var options = new RouteEngineOptions { EnableTelemetry = false, EnableMetrics = false };
        _context = new RouteContext(options: options);

        IExchange? received = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://no-tel-in")
                .Process(ex => received = ex)
                .To("direct://no-tel-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://no-tel-out")
                .Process(_ => { });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://no-tel-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "test" }));

        received.Should().NotBeNull();
    }

    [Fact]
    public async Task Engine_WithThrowOnCompilationError_False_SkipsBadRoutes()
    {
        var options = new RouteEngineOptions { ThrowOnCompilationError = false };
        _context = new RouteContext(options: options);

        // Add a broken route (no From)
        _context.AddRoutes(r =>
        {
            r.From("direct://good-route")
                .Process(_ => { });
        });

        // This should not throw even with compilation issues
        await _context.Start();

        _context.Routes.Should().HaveCount(1);
    }
}
