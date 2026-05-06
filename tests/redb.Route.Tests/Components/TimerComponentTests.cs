using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Components;

/// <summary>
/// Tests for <see cref="TimerComponent"/>, <see cref="TimerEndpoint"/>,
/// <see cref="TimerConsumer"/>, and <see cref="TimerEndpointOptions"/>.
/// </summary>
public class TimerComponentTests
{
    [Fact]
    public void Scheme_IsTimer()
    {
        var component = new TimerComponent();

        component.Scheme.Should().Be("timer");
    }

    [Fact]
    public void CreateEndpoint_ReturnsTimerEndpoint()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://heartbeat?period=500");

        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().BeOfType<TimerEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new TimerComponent();

        var act = () => component.CreateEndpoint(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Options unit tests (direct construction) ──

    [Fact]
    public void Options_DefaultValues()
    {
        var opts = new TimerEndpointOptions();

        opts.Period.Should().Be(1000);
        opts.Delay.Should().Be(0);
        opts.RepeatCount.Should().Be(-1);
    }

    [Fact]
    public void Options_Validate_ThrowsOnZeroPeriod()
    {
        var opts = new TimerEndpointOptions { Period = 0 };

        var act = () => opts.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Validate_ThrowsOnNegativePeriod()
    {
        var opts = new TimerEndpointOptions { Period = -100 };

        var act = () => opts.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Validate_PassesForValidPeriod()
    {
        var opts = new TimerEndpointOptions { Period = 500 };

        var act = () => opts.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void CreateEndpoint_InvalidPeriod_Throws()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://tick?period=0");

        var act = () => component.CreateEndpoint(uri);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateEndpoint_NegativePeriod_Throws()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://bad?period=-100");

        var act = () => component.CreateEndpoint(uri);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateProducer_ThrowsNotSupported()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://tick");
        var endpoint = component.CreateEndpoint(uri);

        var act = () => endpoint.CreateProducer();

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void CreateConsumer_ReturnsTimerConsumer()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://tick?repeatCount=1");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();

        var consumer = endpoint.CreateConsumer(processor);

        consumer.Should().BeOfType<TimerConsumer>();
    }

    [Fact]
    public void CreateConsumer_NullProcessor_Throws()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://tick");
        var endpoint = component.CreateEndpoint(uri);

        var act = () => endpoint.CreateConsumer(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Consumer_FiresProcessorRepeatCountTimes()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://test?period=50&repeatCount=3");
        var endpoint = component.CreateEndpoint(uri);

        var exchanges = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => exchanges.Add(ci.Arg<IExchange>()));

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        // Wait for timer to complete (3 fires × 50ms + margin)
        await Task.Delay(500);
        await consumer.Stop();

        exchanges.Should().HaveCount(3);
    }

    [Fact]
    public async Task Consumer_SetsTimerHeaders()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://myTimer?period=50&repeatCount=1");
        var endpoint = component.CreateEndpoint(uri);

        IExchange? captured = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => captured = ci.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(300);
        await consumer.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey("CamelTimerName");
        captured.In.Headers["CamelTimerName"].Should().Be("myTimer");
        captured.In.Headers.Should().ContainKey("CamelTimerFiredTime");
        captured.In.Headers.Should().ContainKey("CamelTimerCounter");
        captured.In.Headers["CamelTimerCounter"].Should().Be(0);
    }

    [Fact]
    public async Task Consumer_RespectsInitialDelay()
    {
        var component = new TimerComponent();
        // Use large delay (2s) so even under heavy CPU load the 200ms early-check has 10x margin
        var uri = EndpointUriParser.Parse("timer://delayed?period=50&delay=2000&repeatCount=1");
        var endpoint = component.CreateEndpoint(uri);

        var firedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => firedTcs.TrySetResult());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        // At 200ms the 2000ms delay hasn't elapsed yet (10x margin)
        var earlyResult = await Task.WhenAny(firedTcs.Task, Task.Delay(200));
        earlyResult.Should().NotBe(firedTcs.Task, "initial delay of 2000ms has not elapsed");

        // Wait for timer to fire (generous 5s timeout)
        var lateResult = await Task.WhenAny(firedTcs.Task, Task.Delay(5000));
        await consumer.Stop();

        lateResult.Should().Be(firedTcs.Task, "timer should have fired after initial delay");
    }

    [Fact]
    public async Task Consumer_StopCancelsLoop()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://infinite?period=50");
        var endpoint = component.CreateEndpoint(uri);

        var count = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => Interlocked.Increment(ref count));

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(200);
        await consumer.Stop();

        var countAfterStop = count;
        await Task.Delay(200);

        // Should not have fired more after stop
        count.Should().Be(countAfterStop);
        countAfterStop.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Consumer_FireCount_TracksCompletedFires()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://counter?period=50&repeatCount=3");
        var endpoint = component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = (TimerConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(500);
        await consumer.Stop();

        consumer.FireCount.Should().Be(3);
    }

    [Fact]
    public async Task Consumer_ExchangePattern_IsInOnly()
    {
        var component = new TimerComponent();
        var uri = EndpointUriParser.Parse("timer://pattern?period=50&repeatCount=1");
        var endpoint = component.CreateEndpoint(uri);

        IExchange? captured = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => captured = ci.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(300);
        await consumer.Stop();

        captured.Should().NotBeNull();
        captured!.Pattern.Should().Be(ExchangePattern.InOnly);
    }
}
