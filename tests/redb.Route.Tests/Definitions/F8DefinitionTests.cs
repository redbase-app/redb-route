using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for W5 F8 — AggregateDefinition, ThrottleDefinition, DebounceDefinition, CircuitBreakerDefinition.
/// </summary>
public class F8DefinitionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Exchange MakeExchange(object? body = null)
        => new Exchange(new Message { Body = body });

    // ── AggregateDefinition — structure ───────────────────────────────────────

    [Fact]
    public void Aggregate_ReturnsAggregateDefinition()
    {
        var route = new RouteDefinition();
        var agg = route.Aggregate(_ => "key", (old, @new) => @new, _ => false);

        agg.Should().BeOfType<AggregateDefinition>();
    }

    [Fact]
    public void Aggregate_SetsParentOnDefinition()
    {
        var route = new RouteDefinition();
        var agg = route.Aggregate(_ => "key", (old, @new) => @new, _ => false);

        agg.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void Aggregate_IsAddedToRouteOutputs()
    {
        var route = new RouteDefinition();
        route.Aggregate(_ => "key", (old, @new) => @new, _ => false);

        route.Outputs.Should().HaveCount(1);
        route.Outputs[0].Should().BeOfType<AggregateDefinition>();
    }

    [Fact]
    public void EndAggregate_ReturnsParentRoute()
    {
        var route = new RouteDefinition();
        var back = route.Aggregate(_ => "k", (o, n) => n, _ => true).EndAggregate();

        back.Should().BeSameAs(route);
    }

    // ── AggregateDefinition — runtime ─────────────────────────────────────────

    [Fact]
    public async Task Aggregate_CompletedGroup_ForwardsToTargetPipeline()
    {
        var received = new List<object?>();
        var route = new RouteDefinition();
        route.Aggregate(
                _ => "key",
                (old, @new) =>
                {
                    var merged = new Exchange(new Message
                    {
                        Body = (old?.In.Body is string s ? s : "") + (@new.In.Body?.ToString() ?? "")
                    });
                    return merged;
                },
                agg => (agg.In.Body as string)?.Length >= 3)
            .Process(e => received.Add(e.In.Body))
            .EndAggregate();

        var proc = route.CreateProcessor(_context);

        await proc.Process(MakeExchange("A"));
        await proc.Process(MakeExchange("B"));
        await proc.Process(MakeExchange("C")); // completion: length >= 3

        received.Should().HaveCount(1);
        received[0].Should().Be("ABC");
    }

    // ── ThrottleDefinition — structure ────────────────────────────────────────

    [Fact]
    public void Throttle_ReturnsThrottleDefinition()
    {
        var route = new RouteDefinition();
        var throttle = route.Throttle(5);

        throttle.Should().BeOfType<ThrottleDefinition>();
    }

    [Fact]
    public void Throttle_SetsParentOnDefinition()
    {
        var route = new RouteDefinition();
        var throttle = route.Throttle(5);

        throttle.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void Throttle_InvalidRate_Throws()
    {
        var route = new RouteDefinition();
        route.Invoking(r => r.Throttle(0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EndThrottle_ReturnsParentRoute()
    {
        var route = new RouteDefinition();
        var back = route.Throttle(5).EndThrottle();

        back.Should().BeSameAs(route);
    }

    // ── ThrottleDefinition — runtime ──────────────────────────────────────────

    [Fact]
    public async Task Throttle_ProcessorCreated_ForwardsExchange()
    {
        var processed = new List<object?>();
        var route = new RouteDefinition();
        route.Throttle(100)
            .Process(e => processed.Add(e.In.Body))
            .EndThrottle();

        var proc = route.CreateProcessor(_context);
        await proc.Process(MakeExchange("hello"));

        processed.Should().ContainSingle().Which.Should().Be("hello");
    }

    // ── DebounceDefinition — structure ────────────────────────────────────────

    [Fact]
    public void Debounce_ReturnsDebounceDefinition()
    {
        var route = new RouteDefinition();
        var deb = route.Debounce(_ => "k", TimeSpan.FromMilliseconds(100));

        deb.Should().BeOfType<DebounceDefinition>();
    }

    [Fact]
    public void Debounce_SetsParentOnDefinition()
    {
        var route = new RouteDefinition();
        var deb = route.Debounce(_ => "k", TimeSpan.FromMilliseconds(100));

        deb.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void EndDebounce_ReturnsParentRoute()
    {
        var route = new RouteDefinition();
        var back = route.Debounce(_ => "k", TimeSpan.FromMilliseconds(50)).EndDebounce();

        back.Should().BeSameAs(route);
    }

    // ── DebounceDefinition — runtime ──────────────────────────────────────────

    [Fact]
    public async Task Debounce_QuietPeriodElapsed_ForwardsLastExchange()
    {
        var received = new List<object?>();
        var route = new RouteDefinition();
        route.Debounce(_ => "key", TimeSpan.FromMilliseconds(50))
            .Process(e => received.Add(e.In.Body))
            .EndDebounce();

        var proc = route.CreateProcessor(_context);

        await proc.Process(MakeExchange("first"));
        await proc.Process(MakeExchange("second"));

        // Wait for quiet period to expire
        await Task.Delay(200);

        received.Should().ContainSingle().Which.Should().Be("second");
    }

    // ── CircuitBreakerDefinition — structure ──────────────────────────────────

    [Fact]
    public void CircuitBreaker_ReturnsCircuitBreakerDefinition()
    {
        var route = new RouteDefinition();
        var cb = route.CircuitBreaker();

        cb.Should().BeOfType<CircuitBreakerDefinition>();
    }

    [Fact]
    public void CircuitBreaker_SetsParentOnDefinition()
    {
        var route = new RouteDefinition();
        var cb = route.CircuitBreaker();

        cb.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void CircuitBreaker_IsAddedToRouteOutputs()
    {
        var route = new RouteDefinition();
        route.CircuitBreaker();

        route.Outputs.Should().HaveCount(1);
        route.Outputs[0].Should().BeOfType<CircuitBreakerDefinition>();
    }

    [Fact]
    public void EndCircuitBreaker_ReturnsParentRoute()
    {
        var route = new RouteDefinition();
        var back = route.CircuitBreaker().EndCircuitBreaker();

        back.Should().BeSameAs(route);
    }

    [Fact]
    public void OnFallback_ReturnsFallbackDefinition()
    {
        var cb = new CircuitBreakerDefinition();
        var fallback = cb.OnFallback();

        fallback.Should().BeOfType<FallbackDefinition>();
        cb.FallbackBlock.Should().BeSameAs(fallback);
    }

    [Fact]
    public void OnFallback_ThrowsIfCalledTwice()
    {
        var cb = new CircuitBreakerDefinition();
        cb.OnFallback();

        cb.Invoking(c => c.OnFallback())
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FallbackDefinition_EndFallback_ReturnsCircuitBreaker()
    {
        var cb = new CircuitBreakerDefinition();
        var back = cb.OnFallback().EndFallback();

        back.Should().BeSameAs(cb);
    }

    [Fact]
    public void FallbackDefinition_EndCircuitBreaker_ReturnsParent()
    {
        var route = new RouteDefinition();
        var cb = route.CircuitBreaker();
        var back = cb.OnFallback().EndCircuitBreaker();

        back.Should().BeSameAs(route);
    }

    // ── CircuitBreakerDefinition — runtime ────────────────────────────────────

    [Fact]
    public async Task CircuitBreaker_Closed_ForwardsExchange()
    {
        var processed = new List<object?>();
        var route = new RouteDefinition();
        route.CircuitBreaker()
            .FailureThreshold(3)
            .Process(e => processed.Add(e.In.Body))
            .EndCircuitBreaker();

        var proc = route.CreateProcessor(_context);
        await proc.Process(MakeExchange("ok"));

        processed.Should().ContainSingle().Which.Should().Be("ok");
    }

    [Fact]
    public async Task CircuitBreaker_Opens_AfterThresholdFailures_UsesFallback()
    {
        var fallbackRan = new List<string>();
        var route = new RouteDefinition();
        route.CircuitBreaker()
            .FailureThreshold(2)
            .ResetTimeout(TimeSpan.FromSeconds(60))
            .Process(_ => throw new InvalidOperationException("bang"))
            .OnFallback()
                .Process(e => fallbackRan.Add("fallback"))
                .EndFallback()
            .EndCircuitBreaker();

        var proc = route.CreateProcessor(_context);

        // Trip the circuit: 2 failures open it
        for (var i = 0; i < 2; i++)
        {
            try { await proc.Process(MakeExchange()); } catch { /* expected */ }
        }

        // Now circuit is open — fallback should run
        await proc.Process(MakeExchange());

        fallbackRan.Should().ContainSingle().Which.Should().Be("fallback");
    }
}
