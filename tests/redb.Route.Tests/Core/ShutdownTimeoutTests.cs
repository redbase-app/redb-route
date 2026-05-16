using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for <see cref="RouteContext.Stop"/> and <see cref="RouteContext.StopRoute(string, TimeSpan?, CancellationToken)"/>
/// shutdown timeout enforcement (Phase 3).
/// </summary>
public sealed class ShutdownTimeoutTests : IDisposable
{
    private readonly RouteContext _ctx;

    public ShutdownTimeoutTests()
    {
        _ctx = new RouteContext("shutdown-test", options: new RouteEngineOptions
        {
            ShutdownTimeout = TimeSpan.FromSeconds(2)
        });
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Stop_CompletesGracefully_WhenConsumersStopWithinTimeout()
    {
        // Arrange — consumer that stops fast
        var consumer = Substitute.For<IConsumer>();
        consumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        consumer.Stop(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _ctx.AddConsumer(consumer);
        await _ctx.Start();

        // Act
        await _ctx.Stop();

        // Assert — Stop was called
        await consumer.Received(1).Stop(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_ForcesShutdown_WhenConsumerExceedsTimeout()
    {
        // Arrange — consumer that blocks forever until CT fires
        var consumer = Substitute.For<IConsumer>();
        consumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        consumer.Stop(Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                return Task.Delay(Timeout.Infinite, ct);
            });

        var options = new RouteEngineOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(200) };
        using var ctx = new RouteContext("timeout-ctx", options: options);
        ctx.AddConsumer(consumer);
        await ctx.Start();

        // Act — should not hang, ShutdownTimeout kicks in
        var stopTask = ctx.Stop();
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));

        // Assert
        completed.Should().BeSameAs(stopTask, "Stop() should complete after ShutdownTimeout, not hang");
    }

    [Fact]
    public async Task Stop_PassesCancellationToken_LinkedToShutdownTimeout()
    {
        // Arrange — consumer that captures the CT
        CancellationToken capturedCt = default;
        var consumer = Substitute.For<IConsumer>();
        consumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        consumer.Stop(Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedCt = ci.Arg<CancellationToken>();
                return Task.CompletedTask;
            });

        var options = new RouteEngineOptions { ShutdownTimeout = TimeSpan.FromSeconds(5) };
        using var ctx = new RouteContext("ct-test", options: options);
        ctx.AddConsumer(consumer);
        await ctx.Start();

        // Act
        await ctx.Stop();

        // Assert — the CT passed to consumer should be cancellable (linked to timeout)
        capturedCt.CanBeCanceled.Should().BeTrue("CT should be linked to ShutdownTimeout");
    }

    [Fact]
    public async Task StopRoute_ThrowsForUnknownRouteId()
    {
        await _ctx.Start();

        var act = () => _ctx.StopRoute("nonexistent-route");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task StopRoute_Overload_AcceptsNullTimeout()
    {
        await _ctx.Start();

        // null timeout should fall back to ShutdownTimeout — but route doesn't exist
        var act = () => _ctx.StopRoute("no-such-route", null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task Stop_MultipleConsumers_AllReceiveLinkedCt()
    {
        // Arrange — two consumers, both capture CT
        var cts = new List<CancellationToken>();

        IConsumer MakeConsumer()
        {
            var c = Substitute.For<IConsumer>();
            c.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            c.Stop(Arg.Any<CancellationToken>()).Returns(ci =>
            {
                cts.Add(ci.Arg<CancellationToken>());
                return Task.CompletedTask;
            });
            return c;
        }

        var options = new RouteEngineOptions { ShutdownTimeout = TimeSpan.FromSeconds(5) };
        using var ctx = new RouteContext("multi-consumer", options: options);
        ctx.AddConsumer(MakeConsumer());
        ctx.AddConsumer(MakeConsumer());
        await ctx.Start();

        // Act
        await ctx.Stop();

        // Assert — both got cancellable tokens
        cts.Should().HaveCount(2);
        cts.Should().AllSatisfy(ct => ct.CanBeCanceled.Should().BeTrue());
    }

    [Fact]
    public async Task Stop_Idempotent_WhenCalledTwice()
    {
        var consumer = Substitute.For<IConsumer>();
        consumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        consumer.Stop(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _ctx.AddConsumer(consumer);
        await _ctx.Start();

        // Act
        await _ctx.Stop();
        await _ctx.Stop(); // second call

        // Assert — Stop() was only called once on consumer (second Stop() exits early)
        await consumer.Received(1).Stop(Arg.Any<CancellationToken>());
    }
}
