using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.ErrorHandling;
using redb.Route.Processors;

namespace redb.Route.Tests.ErrorHandling;

/// <summary>
/// Tests for <see cref="DeadLetterProcessor"/>.
/// </summary>
public class DeadLetterProcessorTests
{
    [Fact]
    public async Task Success_PassesThrough_NoDlc()
    {
        IExchange? captured = null;
        var inner = new DelegateProcessor(ex => ex.In.Body = "processed");
        var dlcTarget = new DelegateProcessor(ex => captured = ex);

        var sut = new DeadLetterProcessor(inner, dlcTarget);
        var exchange = new Exchange(new Message { Body = "original" });

        await sut.Process(exchange);

        exchange.In.Body.Should().Be("processed");
        captured.Should().BeNull("dead letter target should not be called on success");
    }

    [Fact]
    public async Task Failure_RoutesToDeadLetter()
    {
        IExchange? capturedDlc = null;
        var inner = new DelegateProcessor(async (_, _) =>
        {
            throw new InvalidOperationException("boom");
        });
        var dlcTarget = new DelegateProcessor(ex => capturedDlc = ex);

        var sut = new DeadLetterProcessor(inner, dlcTarget);
        var exchange = new Exchange(new Message { Body = "original" });

        await sut.Process(exchange);

        // Exchange should be routed to DLC, not re-thrown
        capturedDlc.Should().NotBeNull();
        exchange.ExceptionHandled.Should().BeTrue();
        exchange.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task Failure_StampsDeadLetterHeaders()
    {
        var inner = new DelegateProcessor(async (_, _) =>
        {
            throw new ArgumentException("bad input");
        });
        var dlcTarget = new DelegateProcessor(_ => { });

        var sut = new DeadLetterProcessor(inner, dlcTarget);
        var exchange = new Exchange(new Message { Body = "data" });

        await sut.Process(exchange);

        exchange.In.Headers.Should().ContainKey("CamelDeadLetterReason");
        exchange.In.Headers["CamelDeadLetterReason"].Should().Be("bad input");

        exchange.In.Headers.Should().ContainKey("CamelDeadLetterExceptionType");
        exchange.In.Headers["CamelDeadLetterExceptionType"].Should().Be(typeof(ArgumentException).FullName);

        exchange.In.Headers.Should().ContainKey("CamelDeadLetterTimestamp");
    }

    [Fact]
    public async Task OperationCanceled_IsNotCaughtByDlc()
    {
        var inner = new DelegateProcessor(async (_, _) =>
        {
            throw new OperationCanceledException();
        });
        var dlcTarget = new DelegateProcessor(_ => { });

        var sut = new DeadLetterProcessor(inner, dlcTarget);
        var exchange = new Exchange(new Message { Body = "data" });

        var act = () => sut.Process(exchange);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── B9: DLC with RetryPolicy ──

    [Fact]
    public async Task RetryPolicy_RetriesBeforeDeadLettering()
    {
        var callCount = 0;
        var inner = new DelegateProcessor(async (_, _) =>
        {
            callCount++;
            if (callCount < 3)
                throw new InvalidOperationException("transient");
        });
        var dlcTarget = new DelegateProcessor(_ => { });

        var policy = RetryPolicy.Fixed(5, TimeSpan.FromMilliseconds(1));
        var sut = new DeadLetterProcessor(inner, dlcTarget, policy);
        var exchange = new Exchange(new Message { Body = "data" });

        await sut.Process(exchange);

        callCount.Should().Be(3, "should succeed on 3rd attempt");
        exchange.ExceptionHandled.Should().BeFalse("no DLQ needed — success");
    }

    [Fact]
    public async Task RetryPolicy_ExhaustedThenDeadLetters()
    {
        IExchange? captured = null;
        var inner = new DelegateProcessor(async (_, _) =>
        {
            throw new InvalidOperationException("always fails");
        });
        var dlcTarget = new DelegateProcessor(ex => captured = ex);

        var policy = RetryPolicy.Fixed(2, TimeSpan.FromMilliseconds(1));
        var sut = new DeadLetterProcessor(inner, dlcTarget, policy);
        var exchange = new Exchange(new Message { Body = "data" });

        await sut.Process(exchange);

        captured.Should().NotBeNull("should route to DLQ after retries exhausted");
        exchange.ExceptionHandled.Should().BeTrue();
        exchange.In.Headers.Should().ContainKey("CamelDeadLetterRedeliveryCount");
        exchange.In.Headers["CamelDeadLetterRedeliveryCount"].Should().Be(2);
    }

    [Fact]
    public async Task RetryPolicy_RespectsRetryableExceptionPredicate()
    {
        var callCount = 0;
        IExchange? captured = null;
        var inner = new DelegateProcessor(async (_, _) =>
        {
            callCount++;
            throw new ArgumentException("not retryable");
        });
        var dlcTarget = new DelegateProcessor(ex => captured = ex);

        var policy = new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            RetryableExceptionPredicate = ex => ex is InvalidOperationException // only IOE is retryable
        };
        var sut = new DeadLetterProcessor(inner, dlcTarget, policy);
        var exchange = new Exchange(new Message { Body = "data" });

        await sut.Process(exchange);

        callCount.Should().Be(1, "ArgumentException is not retryable — no retries");
        captured.Should().NotBeNull();
    }
}
