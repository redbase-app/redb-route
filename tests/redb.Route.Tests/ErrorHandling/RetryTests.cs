using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.ErrorHandling;
using redb.Route.Processors;

namespace redb.Route.Tests.ErrorHandling;

/// <summary>
/// Tests for <see cref="RetryPolicy"/>.
/// </summary>
public class RetryPolicyTests
{
    [Fact]
    public void None_HasZeroRetries()
    {
        var policy = RetryPolicy.None;

        policy.MaxRetries.Should().Be(0);
    }

    [Fact]
    public void Fixed_HasConstantDelay()
    {
        var delay = TimeSpan.FromMilliseconds(200);
        var policy = RetryPolicy.Fixed(3, delay);

        policy.MaxRetries.Should().Be(3);
        policy.GetDelay(0).Should().Be(delay);
        policy.GetDelay(1).Should().Be(delay);
        policy.GetDelay(2).Should().Be(delay);
    }

    [Fact]
    public void Exponential_IncreasesDelay()
    {
        var policy = RetryPolicy.Exponential(5, TimeSpan.FromMilliseconds(100));

        var d0 = policy.GetDelay(0);
        var d1 = policy.GetDelay(1);
        var d2 = policy.GetDelay(2);

        d0.TotalMilliseconds.Should().Be(100);
        d1.TotalMilliseconds.Should().Be(200);
        d2.TotalMilliseconds.Should().Be(400);
    }

    [Fact]
    public void Exponential_RespectsMaxDelay()
    {
        var policy = RetryPolicy.Exponential(
            10,
            TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(5));

        // After enough attempts, delay should be capped
        var d5 = policy.GetDelay(5); // 1 * 2^5 = 32s → capped at 5s
        d5.TotalSeconds.Should().Be(5);
    }

    [Fact]
    public void ShouldRetry_DefaultRetryAllExceptions()
    {
        var policy = RetryPolicy.Fixed(3, TimeSpan.FromMilliseconds(10));

        policy.ShouldRetry(new InvalidOperationException()).Should().BeTrue();
        policy.ShouldRetry(new TimeoutException()).Should().BeTrue();
    }

    [Fact]
    public void ShouldRetry_RespectsCustomPredicate()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 3,
            InitialDelay = TimeSpan.FromMilliseconds(10),
            RetryableExceptionPredicate = ex => ex is TimeoutException
        };

        policy.ShouldRetry(new TimeoutException()).Should().BeTrue();
        policy.ShouldRetry(new InvalidOperationException()).Should().BeFalse();
    }

    // ── B1: CollisionAvoidanceFactor (jitter) ──

    [Fact]
    public void Jitter_ZeroFactor_NoDelta()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 3,
            InitialDelay = TimeSpan.FromMilliseconds(100),
            BackoffMultiplier = 1.0,
            CollisionAvoidanceFactor = 0.0
        };

        // With zero jitter, delay is deterministic
        policy.GetDelay(0).TotalMilliseconds.Should().Be(100);
        policy.GetDelay(1).TotalMilliseconds.Should().Be(100);
    }

    [Fact]
    public void Jitter_WithFactor_ProducesVariation()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 100,
            InitialDelay = TimeSpan.FromMilliseconds(1000),
            BackoffMultiplier = 1.0,
            MaxDelay = TimeSpan.FromSeconds(60),
            CollisionAvoidanceFactor = 0.15
        };

        // Run many times — at least one should differ from the base 1000ms
        var delays = Enumerable.Range(0, 50).Select(i => policy.GetDelay(0).TotalMilliseconds).ToList();
        delays.Should().Contain(d => d != 1000, "jitter should produce variation");
        // All should be within ±15% (850..1150)
        delays.Should().OnlyContain(d => d >= 850 && d <= 1150);
    }

    // ── B2: DelayPattern ──

    [Fact]
    public void DelayPattern_OverridesExponential()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 10,
            InitialDelay = TimeSpan.FromMilliseconds(100),
            BackoffMultiplier = 2.0,
            DelayPattern = "1:500;3:2000;7:10000"
        };

        // Attempt 0 → 1-based=1, matches key "1" → 500ms
        policy.GetDelay(0).TotalMilliseconds.Should().Be(500);
        // Attempt 1 → 1-based=2, highest key ≤ 2 is "1" → 500ms
        policy.GetDelay(1).TotalMilliseconds.Should().Be(500);
        // Attempt 2 → 1-based=3, matches key "3" → 2000ms
        policy.GetDelay(2).TotalMilliseconds.Should().Be(2000);
        // Attempt 5 → 1-based=6, highest key ≤ 6 is "3" → 2000ms
        policy.GetDelay(5).TotalMilliseconds.Should().Be(2000);
        // Attempt 6 → 1-based=7, matches key "7" → 10000ms
        policy.GetDelay(6).TotalMilliseconds.Should().Be(10000);
    }

    [Fact]
    public void DelayPattern_NoMatchFallsBackToStandard()
    {
        // Pattern starts at attempt 5, so attempts 0-3 use standard calculation
        var policy = new RetryPolicy
        {
            MaxRetries = 10,
            InitialDelay = TimeSpan.FromMilliseconds(100),
            BackoffMultiplier = 1.0,
            DelayPattern = "5:9999"
        };

        // 1-based=1, no key ≤ 1 in pattern → standard calc → 100ms
        policy.GetDelay(0).TotalMilliseconds.Should().Be(100);
        // 1-based=5, matches → 9999ms
        policy.GetDelay(4).TotalMilliseconds.Should().Be(9999);
    }
}

/// <summary>
/// Tests for <see cref="RetryProcessor"/>.
/// </summary>
public class RetryProcessorTests
{
    [Fact]
    public async Task Succeeds_OnFirstAttempt_NoRetry()
    {
        var callCount = 0;
        var inner = new DelegateProcessor(_ => callCount++);
        var sut = new RetryProcessor(inner, RetryPolicy.Fixed(3, TimeSpan.FromMilliseconds(1)));
        var exchange = new Exchange(new Message { Body = "test" });

        await sut.Process(exchange);

        callCount.Should().Be(1);
    }

    [Fact]
    public async Task Retries_OnFailure_ThenSucceeds()
    {
        var callCount = 0;
        var inner = new DelegateProcessor(async (_, _) =>
        {
            callCount++;
            if (callCount < 3)
                throw new InvalidOperationException("transient");
        });

        var sut = new RetryProcessor(inner, RetryPolicy.Fixed(5, TimeSpan.FromMilliseconds(1)));
        var exchange = new Exchange(new Message { Body = "test" });

        await sut.Process(exchange);

        callCount.Should().Be(3);
        exchange.Exception.Should().BeNull();
    }

    [Fact]
    public async Task Throws_WhenRetriesExhausted()
    {
        var inner = new DelegateProcessor(async (_, _) =>
        {
            throw new InvalidOperationException("permanent");
        });

        var sut = new RetryProcessor(inner, RetryPolicy.Fixed(2, TimeSpan.FromMilliseconds(1)));
        var exchange = new Exchange(new Message { Body = "test" });

        var act = () => sut.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("permanent");
    }

    [Fact]
    public async Task DoesNotRetry_OperationCanceled()
    {
        var callCount = 0;
        var inner = new DelegateProcessor(async (_, _) =>
        {
            callCount++;
            throw new OperationCanceledException();
        });

        var sut = new RetryProcessor(inner, RetryPolicy.Fixed(5, TimeSpan.FromMilliseconds(1)));
        var exchange = new Exchange(new Message { Body = "test" });

        var act = () => sut.Process(exchange);

        await act.Should().ThrowAsync<OperationCanceledException>();
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task DoesNotRetry_WhenPredicateRejects()
    {
        var callCount = 0;
        var inner = new DelegateProcessor(async (_, _) =>
        {
            callCount++;
            throw new ArgumentException("bad");
        });

        var policy = new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            RetryableExceptionPredicate = ex => ex is TimeoutException // Only retry timeouts
        };
        var sut = new RetryProcessor(inner, policy);
        var exchange = new Exchange(new Message { Body = "test" });

        var act = () => sut.Process(exchange);

        await act.Should().ThrowAsync<ArgumentException>();
        callCount.Should().Be(1);
    }
}
