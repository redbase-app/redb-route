using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Validation;
using Xunit;

namespace redb.Route.Tests.Validation;

public class RouteDefinitionValidatorTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static RouteDefinition CreateRoute(string? routeId = null)
    {
        var def = new RouteDefinition();
        if (routeId is not null) def.RouteId(routeId);
        def.From("direct://input");
        return def;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Valid routes pass without errors
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_EmptyRoute_DoesNotThrow()
    {
        var def = CreateRoute("empty-route");
        def.To("direct://out");

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ValidThrottle_DoesNotThrow()
    {
        var def = CreateRoute("throttle-ok");
        def.Throttle(100).To("direct://out");

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ValidCircuitBreaker_DoesNotThrow()
    {
        var def = CreateRoute("cb-ok");
        def._steps.Add(new CircuitBreakerStep(
            FailureThreshold: 5,
            ResetTimeout: TimeSpan.FromSeconds(30),
            HalfOpenMaxCalls: 2));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().NotThrow();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ScatterGather validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_ScatterGather_NoRecipients_Fails()
    {
        var def = CreateRoute("sg-no-recipients");
        def._steps.Add(new ScatterGatherStep(
            StaticRecipients: null,
            DynamicRecipients: null,
            AggregationStrategy: (acc, cur) => cur));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("recipient"));
    }

    [Fact]
    public void Validate_ScatterGather_EmptyStaticRecipients_Fails()
    {
        var def = CreateRoute("sg-empty");
        def._steps.Add(new ScatterGatherStep(
            StaticRecipients: Array.Empty<string>(),
            DynamicRecipients: null,
            AggregationStrategy: (acc, cur) => cur));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("recipient"));
    }

    [Fact]
    public void Validate_ScatterGather_NoAggregation_Fails()
    {
        var def = CreateRoute("sg-no-agg");
        def._steps.Add(new ScatterGatherStep(
            StaticRecipients: new[] { "direct://a" },
            DynamicRecipients: null,
            AggregationStrategy: null!));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("AggregationStrategy"));
    }

    [Fact]
    public void Validate_ScatterGather_NegativeParallelism_Fails()
    {
        var def = CreateRoute("sg-neg-par");
        def._steps.Add(new ScatterGatherStep(
            StaticRecipients: new[] { "direct://a" },
            DynamicRecipients: null,
            AggregationStrategy: (acc, cur) => cur,
            MaxDegreeOfParallelism: -1));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("MaxDegreeOfParallelism"));
    }

    [Fact]
    public void Validate_ScatterGather_WithDynamicRecipients_Passes()
    {
        var def = CreateRoute("sg-dynamic");
        def._steps.Add(new ScatterGatherStep(
            StaticRecipients: null,
            DynamicRecipients: ex => new[] { "direct://a" },
            AggregationStrategy: (acc, cur) => cur));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().NotThrow();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CircuitBreaker validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_CircuitBreaker_ZeroThreshold_Fails()
    {
        var def = CreateRoute("cb-zero");
        def._steps.Add(new CircuitBreakerStep(FailureThreshold: 0));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("FailureThreshold"));
    }

    [Fact]
    public void Validate_CircuitBreaker_NegativeResetTimeout_Fails()
    {
        var def = CreateRoute("cb-neg-timeout");
        def._steps.Add(new CircuitBreakerStep(
            FailureThreshold: 3,
            ResetTimeout: TimeSpan.FromSeconds(-1)));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("ResetTimeout"));
    }

    [Fact]
    public void Validate_CircuitBreaker_ZeroHalfOpenMaxCalls_Fails()
    {
        var def = CreateRoute("cb-zero-half");
        def._steps.Add(new CircuitBreakerStep(
            FailureThreshold: 3,
            HalfOpenMaxCalls: 0));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("HalfOpenMaxCalls"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Throttle / KeyedThrottle validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_Throttle_ZeroMaxPerPeriod_Fails()
    {
        var def = CreateRoute("throttle-zero");
        def._steps.Add(new ThrottleStep(MaxPerPeriod: 0));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("MaxPerPeriod"));
    }

    [Fact]
    public void Validate_KeyedThrottle_NegativeMaxPerPeriod_Fails()
    {
        var def = CreateRoute("keyed-neg");
        def._steps.Add(new KeyedThrottleStep(
            KeyExtractor: ex => "key",
            MaxPerPeriod: -5));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("MaxPerPeriod"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Debounce validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_Debounce_ZeroQuietPeriod_Fails()
    {
        var def = CreateRoute("debounce-zero");
        def._steps.Add(new DebounceStep(
            KeyExtractor: ex => "key",
            QuietPeriod: TimeSpan.Zero));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("QuietPeriod"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Resequencer validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_Resequencer_ZeroBatchSize_Fails()
    {
        var def = CreateRoute("reseq-zero");
        def._steps.Add(new ResequenceStep(
            KeySelector: ex => 1L,
            BatchSize: 0));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("BatchSize"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  LoadBalancer validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_LoadBalancer_NoEndpoints_Fails()
    {
        var strategy = Substitute.For<ILoadBalancerStrategy>();
        var def = CreateRoute("lb-empty");
        def._steps.Add(new LoadBalanceStep(strategy, new List<string>()));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("endpoint"));
    }

    [Fact]
    public void Validate_LoadBalancer_NullEndpoints_Fails()
    {
        var strategy = Substitute.For<ILoadBalancerStrategy>();
        var def = CreateRoute("lb-null");
        def._steps.Add(new LoadBalanceStep(strategy, null!));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("endpoint"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Recursive sub-step validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_InvalidStepInsideTracedStep_Fails()
    {
        var def = CreateRoute("traced-inner");
        def._steps.Add(new TracedStep(
            SpanName: "span",
            SubSteps: new RouteStep[] { new ThrottleStep(0) }));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("MaxPerPeriod"));
    }

    [Fact]
    public void Validate_InvalidStepInsideMeteredStep_Fails()
    {
        var def = CreateRoute("metered-inner");
        def._steps.Add(new MeteredStep(
            StepName: "meter",
            SubSteps: new RouteStep[] { new ThrottleStep(-1) }));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("MaxPerPeriod"));
    }

    [Fact]
    public void Validate_InvalidStepInsideCircuitBreakerFallback_Fails()
    {
        var def = CreateRoute("cb-fallback-inner");
        def._steps.Add(new CircuitBreakerStep(
            FailureThreshold: 3,
            FallbackSteps: new RouteStep[] { new ThrottleStep(0) }));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().Contain(e => e.Contains("MaxPerPeriod"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Multiple errors aggregation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_MultipleInvalidSteps_CollectsAllErrors()
    {
        var def = CreateRoute("multi-errors");
        def._steps.Add(new ThrottleStep(0));
        def._steps.Add(new DebounceStep(ex => "k", TimeSpan.Zero));
        def._steps.Add(new ResequenceStep(ex => 1L, BatchSize: -1));

        var act = () => RouteDefinitionValidator.Validate(def);

        act.Should().Throw<RouteValidationException>()
            .Which.Errors.Should().HaveCount(3);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Exception properties
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_ExceptionContainsRouteId()
    {
        var def = CreateRoute("my-route-id");
        def._steps.Add(new ThrottleStep(0));

        var act = () => RouteDefinitionValidator.Validate(def);

        var ex = act.Should().Throw<RouteValidationException>().Which;
        ex.RouteId.Should().Be("my-route-id");
        ex.Message.Should().Contain("my-route-id");
    }

    [Fact]
    public void Validate_UnnamedRoute_ExceptionShowsUnnamed()
    {
        var def = new RouteDefinition();
        def.From("direct://input");
        def._steps.Add(new ThrottleStep(0));

        var act = () => RouteDefinitionValidator.Validate(def);

        var ex = act.Should().Throw<RouteValidationException>().Which;
        ex.RouteId.Should().BeNull();
        ex.Message.Should().Contain("(unnamed)");
    }
}
