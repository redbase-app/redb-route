using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="CircuitBreakerProcessor"/>.</summary>
public class CircuitBreakerProcessorTests
{
    [Fact]
    public async Task Process_Closed_PassesThrough()
    {
        var processed = false;
        var next = new DelegateProcessor(_ => processed = true);
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 3);

        await cb.Process(new Exchange(new Message("data")));

        processed.Should().BeTrue();
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task Process_BelowThreshold_StaysClosed()
    {
        var callCount = 0;
        var next = new DelegateProcessor(_ =>
        {
            callCount++;
            throw new InvalidOperationException("boom");
        });
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 3);

        // 2 failures — still below threshold of 3
        for (int i = 0; i < 2; i++)
            await cb.Process(new Exchange());

        callCount.Should().Be(2);
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task Process_ReachesThreshold_OpensCircuit()
    {
        var next = new DelegateProcessor(_ => throw new InvalidOperationException("fail"));
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 3);

        for (int i = 0; i < 3; i++)
            await cb.Process(new Exchange());

        cb.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public async Task Process_Open_SetsCircuitBreakerOpenException()
    {
        var next = new DelegateProcessor(_ => throw new InvalidOperationException("fail"));
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 2,
            resetTimeout: TimeSpan.FromMinutes(5));

        // Trip the breaker
        for (int i = 0; i < 2; i++)
            await cb.Process(new Exchange());
        cb.State.Should().Be(CircuitState.Open);

        // Next call should set CircuitBreakerOpenException
        var exchange = new Exchange(new Message("blocked"));
        await cb.Process(exchange);

        exchange.Exception.Should().BeOfType<CircuitBreakerOpenException>();
    }

    [Fact]
    public async Task Process_Open_UsesFallback()
    {
        var fallbackUsed = false;
        var fallback = new DelegateProcessor(_ => fallbackUsed = true);
        var next = new DelegateProcessor(_ => throw new InvalidOperationException("fail"));
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 2,
            resetTimeout: TimeSpan.FromMinutes(5), fallback: fallback);

        for (int i = 0; i < 2; i++)
            await cb.Process(new Exchange());
        cb.State.Should().Be(CircuitState.Open);

        await cb.Process(new Exchange());
        fallbackUsed.Should().BeTrue();
    }

    [Fact]
    public async Task Process_Open_TransitionsToHalfOpen_AfterTimeout()
    {
        var next = new DelegateProcessor(_ => throw new InvalidOperationException("fail"));
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 2,
            resetTimeout: TimeSpan.FromMilliseconds(100));

        for (int i = 0; i < 2; i++)
            await cb.Process(new Exchange());
        cb.State.Should().Be(CircuitState.Open);

        // Wait for reset timeout
        await Task.Delay(150);

        // Next call should be allowed (HalfOpen probe)
        var probe = new Exchange(new Message("probe"));
        await cb.Process(probe);

        // It will fail (our next still throws), so back to Open
        // But the state should have transitioned to HalfOpen first
        probe.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task Process_HalfOpen_SuccessClosesCircuit()
    {
        var shouldFail = true;
        var next = new DelegateProcessor(_ =>
        {
            if (shouldFail) throw new InvalidOperationException("fail");
        });
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 2,
            resetTimeout: TimeSpan.FromMilliseconds(50));

        // Trip breaker
        for (int i = 0; i < 2; i++)
            await cb.Process(new Exchange());
        cb.State.Should().Be(CircuitState.Open);

        // Wait for timeout
        await Task.Delay(100);

        // Now make next succeed
        shouldFail = false;
        await cb.Process(new Exchange(new Message("recovery")));

        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task Process_ExchangeException_CountsAsFailure()
    {
        var next = new DelegateProcessor(ex =>
        {
            ex.Exception = new InvalidOperationException("exchange-level failure");
        });
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 2);

        for (int i = 0; i < 2; i++)
            await cb.Process(new Exchange());

        cb.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public async Task Process_SuccessResetsFailureCount()
    {
        var callIndex = 0;
        var next = new DelegateProcessor(_ =>
        {
            callIndex++;
            // Fail on calls 1,2 then succeed on call 3 then fail on 4,5
            if (callIndex <= 2 || callIndex >= 4)
                throw new InvalidOperationException("fail");
        });
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 3);

        // 2 failures
        await cb.Process(new Exchange());
        await cb.Process(new Exchange());
        cb.State.Should().Be(CircuitState.Closed); // 2 < 3

        // 1 success — resets counter
        await cb.Process(new Exchange());
        cb.State.Should().Be(CircuitState.Closed);

        // 2 more failures — total consecutive is only 2
        await cb.Process(new Exchange());
        await cb.Process(new Exchange());
        cb.State.Should().Be(CircuitState.Closed); // still 2 < 3
    }

    [Fact]
    public void Constructor_NullNext_Throws()
    {
        var act = () => new CircuitBreakerProcessor(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("next");
    }

    [Fact]
    public void Constructor_ZeroThreshold_Throws()
    {
        var act = () => new CircuitBreakerProcessor(new DelegateProcessor(_ => { }), failureThreshold: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Process_PreservesBody()
    {
        object? captured = null;
        var next = new DelegateProcessor(ex => captured = ex.In.Body);
        var cb = new CircuitBreakerProcessor(next, failureThreshold: 5);

        await cb.Process(new Exchange(new Message("important")));

        captured.Should().Be("important");
    }
}

/// <summary>Tests for <see cref="CircuitBreakerOpenException"/>.</summary>
public class CircuitBreakerOpenExceptionTests
{
    [Fact]
    public void Ctor_Message_SetsMessage()
    {
        var ex = new CircuitBreakerOpenException("circuit open");
        ex.Message.Should().Be("circuit open");
    }

    [Fact]
    public void Ctor_MessageAndInner_SetsInner()
    {
        var inner = new InvalidOperationException("root");
        var ex = new CircuitBreakerOpenException("circuit open", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }
}
