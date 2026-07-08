using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// Tests for <see cref="InstrumentedProcessor"/> and <see cref="MeteredProcessor"/>.
/// </summary>
[Collection("Telemetry")]
public class TelemetryProcessorTests
{
    [Fact]
    public async Task InstrumentedProcessor_DelegatesToInner()
    {
        var called = false;
        var inner = new DelegateProcessor(_ => called = true);
        var sut = new InstrumentedProcessor(inner, "test-op");

        var exchange = new Exchange(new Message { Body = "data" });
        await sut.Process(exchange);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task InstrumentedProcessor_PropagatesException()
    {
        var inner = new DelegateProcessor(async (_, _) =>
        {
            throw new InvalidOperationException("traced failure");
        });
        var sut = new InstrumentedProcessor(inner, "failing-op");

        var exchange = new Exchange(new Message { Body = "data" });
        var act = () => sut.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("traced failure");
    }

    [Fact]
    public async Task MeteredProcessor_DelegatesToInner()
    {
        var called = false;
        var inner = new DelegateProcessor(_ => called = true);
        var sut = new MeteredProcessor(inner, "test-route");

        var exchange = new Exchange(new Message { Body = "data" });
        await sut.Process(exchange);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task MeteredProcessor_PropagatesException()
    {
        var inner = new DelegateProcessor(async (_, _) =>
        {
            throw new InvalidOperationException("metered failure");
        });
        var sut = new MeteredProcessor(inner, "test-route");

        var exchange = new Exchange(new Message { Body = "data" });
        var act = () => sut.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("metered failure");
    }

    [Fact]
    public void RouteActivitySource_HasCorrectName()
    {
        RouteActivitySource.Source.Name.Should().Be("redb.Route");
    }

    [Fact]
    public void RouteMetrics_MeterName_IsCorrect()
    {
        RouteMetrics.MeterName.Should().Be("redb.Route");
    }
}
