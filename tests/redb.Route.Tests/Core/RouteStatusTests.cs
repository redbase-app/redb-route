using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for expanded RouteStatus transitions (Phase 4).
/// </summary>
public class RouteStatusTests : IAsyncDisposable
{
    private readonly RouteContext _context;

    public RouteStatusTests()
    {
        _context = new RouteContext(options: new RouteEngineOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(500)
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static CompiledRoute MakeRoute(string id, IConsumer consumer, bool autoStart = true) =>
        new(id, $"direct://{id}", new RouteDefinition(),
            new PipelineProcessor(), consumer, Substitute.For<IEndpoint>())
        { AutoStart = autoStart };

    [Fact]
    public void NewRoute_HasStoppedStatus()
    {
        var route = MakeRoute("test", Substitute.For<IConsumer>());
        route.Status.Should().Be(RouteStatus.Stopped);
    }

    [Fact]
    public async Task StartRoute_SetsStartedOnSuccess()
    {
        var consumer = Substitute.For<IConsumer>();
        consumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _context.AddConsumer(consumer);
        _context.AddRoute(MakeRoute("start-ok", consumer));

        await _context.StartRoute("start-ok");

        _context.GetRoute("start-ok")!.Status.Should().Be(RouteStatus.Started);
    }

    [Fact]
    public async Task StartRoute_SetsErroredOnFailure()
    {
        var consumer = Substitute.For<IConsumer>();
        consumer.Start(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("connection failed")));

        _context.AddConsumer(consumer);
        _context.AddRoute(MakeRoute("start-fail", consumer));

        var act = () => _context.StartRoute("start-fail");
        await act.Should().ThrowAsync<InvalidOperationException>();

        _context.GetRoute("start-fail")!.Status.Should().Be(RouteStatus.Errored);
    }

    [Fact]
    public async Task StopRoute_SetsSuspendingThenStopped()
    {
        var consumer = Substitute.For<IConsumer>();
        RouteStatus? statusDuringStop = null;

        consumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        consumer.Stop(Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                statusDuringStop = _context.GetRoute("suspend-test")!.Status;
                return Task.CompletedTask;
            });

        _context.AddConsumer(consumer);
        _context.AddRoute(MakeRoute("suspend-test", consumer));

        await _context.StartRoute("suspend-test");
        await _context.StopRoute("suspend-test");

        statusDuringStop.Should().Be(RouteStatus.Suspended);
        _context.GetRoute("suspend-test")!.Status.Should().Be(RouteStatus.Stopped);
    }

    [Fact]
    public async Task StopRoute_SetsStoppingOnTimeout()
    {
        var consumer = Substitute.For<IConsumer>();

        consumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        consumer.Stop(Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var token = ci.Arg<CancellationToken>();
                await Task.Delay(Timeout.Infinite, token);
            });

        _context.AddConsumer(consumer);
        _context.AddRoute(MakeRoute("timeout-test", consumer));

        await _context.StartRoute("timeout-test");
        await _context.StopRoute("timeout-test", TimeSpan.FromMilliseconds(100));

        _context.GetRoute("timeout-test")!.Status.Should().Be(RouteStatus.Stopped);
    }

    [Fact]
    public async Task Stop_MarksRoutesSuspendingDuringShutdown()
    {
        var consumer = Substitute.For<IConsumer>();
        RouteStatus? capturedStatus = null;

        consumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        consumer.Stop(Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedStatus = _context.Routes[0].Status;
                return Task.CompletedTask;
            });

        _context.AddConsumer(consumer);
        _context.AddRoute(MakeRoute("bulk-suspend", consumer));

        await _context.Start();

        _context.Routes[0].Status.Should().Be(RouteStatus.Started);

        await _context.Stop();

        capturedStatus.Should().Be(RouteStatus.Suspended);
    }

    [Fact]
    public async Task Start_MarksFailedRoutesAsErrored()
    {
        var goodConsumer = Substitute.For<IConsumer>();
        var badConsumer = Substitute.For<IConsumer>();

        goodConsumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        badConsumer.Start(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        _context.AddConsumer(goodConsumer);
        _context.AddConsumer(badConsumer);

        _context.AddRoute(MakeRoute("good-route", goodConsumer));
        _context.AddRoute(MakeRoute("bad-route", badConsumer));

        await _context.Start();

        _context.GetRoute("good-route")!.Status.Should().Be(RouteStatus.Started);
        _context.GetRoute("bad-route")!.Status.Should().Be(RouteStatus.Errored);
    }

    [Fact]
    public void RouteStatus_AllValuesExist()
    {
        var values = Enum.GetValues<RouteStatus>();
        values.Should().HaveCount(7);
        values.Should().Contain(RouteStatus.Stopped);
        values.Should().Contain(RouteStatus.Started);
        values.Should().Contain(RouteStatus.Starting);
        values.Should().Contain(RouteStatus.Suspending);
        values.Should().Contain(RouteStatus.Suspended);
        values.Should().Contain(RouteStatus.Stopping);
        values.Should().Contain(RouteStatus.Errored);
    }
}
