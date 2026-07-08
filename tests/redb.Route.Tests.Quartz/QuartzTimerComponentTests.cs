using redb.Route.Core;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// Tests for QuartzTimerComponent, QuartzTimerEndpoint, QuartzTimerEndpointOptions, and QuartzTimerConsumer.
/// </summary>
public class QuartzTimerComponentTests
{
    // ── Component ──

    [Fact]
    public void Scheme_IsQtimer()
    {
        var component = new QuartzTimerComponent();
        component.Scheme.Should().Be("qtimer");
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new QuartzTimerComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsQuartzTimerEndpoint()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://heartbeat?period=500");
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<QuartzTimerEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_DefaultPeriod()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://heartbeat");
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<QuartzTimerEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_ZeroPeriod_Throws()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://broken?period=0");
        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateEndpoint_NegativePeriod_Throws()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://broken?period=-100");
        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Options ──

    [Fact]
    public void Options_DefaultValues()
    {
        var opts = new QuartzTimerEndpointOptions();
        opts.Period.Should().Be(1000);
        opts.Delay.Should().Be(0);
        opts.FixedRate.Should().BeFalse();
        opts.Threads.Should().Be(1);
    }

    [Fact]
    public void Options_Validate_ZeroPeriod_Throws()
    {
        var opts = new QuartzTimerEndpointOptions { Period = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Validate_NegativeThreads_Throws()
    {
        var opts = new QuartzTimerEndpointOptions { Threads = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Validate_ValidOptions_Passes()
    {
        var opts = new QuartzTimerEndpointOptions { Period = 500, Threads = 3, FixedRate = true, Delay = 100 };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Options_BindFromUri_ParsesAllParameters()
    {
        var uri = EndpointUriParser.Parse("qtimer://tick?period=250&delay=100&fixedRate=true&threads=2");
        var opts = new QuartzTimerEndpointOptions();
        opts.BindFromUri(uri.RawParameters);
        opts.Period.Should().Be(250);
        opts.Delay.Should().Be(100);
        opts.FixedRate.Should().BeTrue();
        opts.Threads.Should().Be(2);
    }

    // ── Endpoint ──

    [Fact]
    public void Endpoint_CreateProducer_ThrowsNotSupported()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://tick?period=500");
        var endpoint = component.CreateEndpoint(uri);
        var act = () => endpoint.CreateProducer();
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Endpoint_CreateConsumer_NullProcessor_Throws()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://tick?period=500");
        var endpoint = component.CreateEndpoint(uri);
        var act = () => endpoint.CreateConsumer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Endpoint_CreateConsumer_ReturnsQuartzTimerConsumer()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://tick?period=500");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<Abstractions.IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);
        consumer.Should().BeOfType<QuartzTimerConsumer>();
    }

    // ── Consumer lifecycle ──

    [Fact]
    public async Task Consumer_StartStop_Lifecycle()
    {
        var component = new QuartzTimerComponent();
        var context = new RouteContext("qtimer-test-1");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("qtimer://tick?period=200");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        QuartzConsumerRegistry.Get(endpoint.Uri.NormalizedKey).Should().NotBeNull();

        await consumer.Stop();

        QuartzConsumerRegistry.Get(endpoint.Uri.NormalizedKey).Should().BeNull();

        context.Dispose();
    }

    [Fact]
    public async Task Consumer_RequiresRouteContext()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://tick?period=200");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<Abstractions.IProcessor>();

        var consumer = endpoint.CreateConsumer(processor);
        var act = () => consumer.Start();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*RouteContext*");
    }

    [Fact]
    public async Task Consumer_FiresProcessor_Periodically()
    {
        var component = new QuartzTimerComponent();
        var context = new RouteContext("qtimer-fire-test");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("qtimer://rapid?period=200");
        var endpoint = component.CreateEndpoint(uri);

        var fireCount = 0;
        Abstractions.IExchange? capturedExchange = null;
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                Interlocked.Increment(ref fireCount);
                capturedExchange = ci.Arg<Abstractions.IExchange>();
            });

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        // Wait for at least 2 fires (period=200ms, so ~1s should be enough)
        for (var i = 0; i < 50 && fireCount < 2; i++)
            await Task.Delay(100);

        await consumer.Stop();
        context.Dispose();

        fireCount.Should().BeGreaterThanOrEqualTo(2, "timer should have fired at least twice");
        capturedExchange.Should().NotBeNull();
        capturedExchange!.Properties.Should().ContainKey("CamelTimerName");
        capturedExchange.Properties["CamelTimerName"].Should().Be("rapid");
        capturedExchange.Properties.Should().ContainKey("CamelTimerPeriod");
        capturedExchange.Properties["CamelTimerPeriod"].Should().Be(200);
        capturedExchange.Properties.Should().ContainKey("CamelTimerFixedRate");
        capturedExchange.Properties["CamelTimerFixedRate"].Should().Be(false);
    }

    [Fact]
    public async Task Consumer_FixedRate_SetsProperty()
    {
        var component = new QuartzTimerComponent();
        var context = new RouteContext("qtimer-fixedrate");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("qtimer://fr?period=200&fixedRate=true");
        var endpoint = component.CreateEndpoint(uri);

        Abstractions.IExchange? captured = null;
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => captured = ci.Arg<Abstractions.IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        for (var i = 0; i < 50 && captured == null; i++)
            await Task.Delay(100);

        await consumer.Stop();
        context.Dispose();

        captured.Should().NotBeNull();
        captured!.Properties["CamelTimerFixedRate"].Should().Be(true);
    }

    [Fact]
    public async Task Consumer_Delay_DelaysFirstFire()
    {
        var component = new QuartzTimerComponent();
        var context = new RouteContext("qtimer-delay");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("qtimer://delayed?period=100&delay=2000");
        var endpoint = component.CreateEndpoint(uri);

        var fireCount = 0;
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => Interlocked.Increment(ref fireCount));

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        // After 500ms with 2000ms delay, should NOT have fired yet
        await Task.Delay(500);
        var countBeforeDelay = fireCount;

        await consumer.Stop();
        context.Dispose();

        countBeforeDelay.Should().Be(0, "with 2000ms delay, no fires should occur within 500ms");
    }

    [Fact]
    public async Task Consumer_ExchangePattern_IsInOnly()
    {
        var component = new QuartzTimerComponent();
        var context = new RouteContext("qtimer-pattern");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("qtimer://chk?period=200");
        var endpoint = component.CreateEndpoint(uri);

        Abstractions.IExchange? captured = null;
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => captured = ci.Arg<Abstractions.IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        for (var i = 0; i < 50 && captured == null; i++)
            await Task.Delay(100);

        await consumer.Stop();
        context.Dispose();

        captured.Should().NotBeNull();
        captured!.Pattern.Should().Be(Abstractions.ExchangePattern.InOnly);
    }

    [Fact]
    public async Task Consumer_QuartzFireTimeProperties_Present()
    {
        var component = new QuartzTimerComponent();
        var context = new RouteContext("qtimer-firetime");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("qtimer://ft?period=200");
        var endpoint = component.CreateEndpoint(uri);

        Abstractions.IExchange? captured = null;
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => captured = ci.Arg<Abstractions.IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        for (var i = 0; i < 50 && captured == null; i++)
            await Task.Delay(100);

        await consumer.Stop();
        context.Dispose();

        captured.Should().NotBeNull();
        captured!.Properties.Should().ContainKey("CamelQuartzFireTime");
        captured.Properties.Should().ContainKey("CamelQuartzScheduledFireTime");
        // NextFireTime should exist for a repeating timer
        captured.Properties.Should().ContainKey("CamelQuartzNextFireTime");
    }

    [Fact]
    public async Task Consumer_StopIdempotent()
    {
        var component = new QuartzTimerComponent();
        var context = new RouteContext("qtimer-idem");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("qtimer://idem?period=500");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
        await consumer.Stop();

        // Double stop
        var act = () => consumer.Stop();
        await act.Should().NotThrowAsync();

        context.Dispose();
    }

    [Fact]
    public async Task Consumer_SharedScheduler_WithCron()
    {
        // Verify that CronComponent and QuartzTimerComponent share the same scheduler
        var cronComponent = new CronComponent();
        var timerComponent = new QuartzTimerComponent();
        var context = new RouteContext("shared-scheduler-test");
        context.AddComponent(cronComponent);
        context.AddComponent(timerComponent);

        var cronUri = EndpointUriParser.Parse("cron://cJob?schedule=* * * * * ?");
        var timerUri = EndpointUriParser.Parse("qtimer://tJob?period=200");
        var cronEp = cronComponent.CreateEndpoint(cronUri);
        var timerEp = timerComponent.CreateEndpoint(timerUri);

        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var cronConsumer = cronEp.CreateConsumer(processor);
        var timerConsumer = timerEp.CreateConsumer(processor);

        await cronConsumer.Start();
        await timerConsumer.Start();

        // Both should share one scheduler
        var scheduler = context.GetService<global::Quartz.IScheduler>();
        scheduler.Should().NotBeNull();

        await cronConsumer.Stop();
        await timerConsumer.Stop();
        context.Dispose();
    }

    [Fact]
    public async Task Consumer_ErrorInProcessor_DoesNotCrash()
    {
        var component = new QuartzTimerComponent();
        var context = new RouteContext("qtimer-error");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("qtimer://err?period=200");
        var endpoint = component.CreateEndpoint(uri);

        var errorThrown = false;
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                errorThrown = true;
                throw new InvalidOperationException("Boom");
            });

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        for (var i = 0; i < 50 && !errorThrown; i++)
            await Task.Delay(100);

        await consumer.Stop();
        context.Dispose();

        errorThrown.Should().BeTrue();
    }

    // ── New Camel-compatible options ──

    [Fact]
    public void Options_DefaultGroupName_IsRedb()
    {
        var opts = new QuartzTimerEndpointOptions();
        opts.GroupName.Should().Be("redb");
    }

    [Fact]
    public void Options_DefaultDeleteJob_IsTrue()
    {
        var opts = new QuartzTimerEndpointOptions();
        opts.DeleteJob.Should().BeTrue();
    }

    [Fact]
    public void Options_DefaultRepeatCount_IsMinusOne()
    {
        var opts = new QuartzTimerEndpointOptions();
        opts.RepeatCount.Should().Be(-1);
    }

    [Fact]
    public void Options_DeleteJobAndPauseJob_BothTrue_Throws()
    {
        var opts = new QuartzTimerEndpointOptions
        {
            DeleteJob = true,
            PauseJob = true
        };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*deleteJob*pauseJob*");
    }

    [Fact]
    public void Options_BindFromUri_ParsesNewParams()
    {
        var uri = EndpointUriParser.Parse("qtimer://hb?period=500&stateful=true&recoverableJob=true&misfireInstruction=SimpleFireNow&repeatCount=10");
        var opts = new QuartzTimerEndpointOptions();
        opts.BindFromUri(uri.RawParameters);
        opts.Period.Should().Be(500);
        opts.Stateful.Should().BeTrue();
        opts.RecoverableJob.Should().BeTrue();
        opts.MisfireInstruction.Should().Be(QuartzMisfirePolicy.SimpleFireNow);
        opts.RepeatCount.Should().Be(10);
    }

    [Fact]
    public void CreateEndpoint_WithGroupInPath_ParsesGroupAndJob()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://monitoring/heartbeat?period=500");
        var endpoint = (QuartzTimerEndpoint)component.CreateEndpoint(uri);
        endpoint.GroupName.Should().Be("monitoring");
        endpoint.JobName.Should().Be("heartbeat");
    }

    [Fact]
    public void CreateEndpoint_SingleName_DefaultGroup()
    {
        var component = new QuartzTimerComponent();
        var uri = EndpointUriParser.Parse("qtimer://heartbeat?period=500");
        var endpoint = (QuartzTimerEndpoint)component.CreateEndpoint(uri);
        endpoint.GroupName.Should().Be("redb");
        endpoint.JobName.Should().Be("heartbeat");
    }

    [Fact]
    public async Task Consumer_FiresWithGroupProperties()
    {
        var component = new QuartzTimerComponent();
        var context = new RouteContext("qtimer-group-test");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("qtimer://monitoring/heartbeat?period=200");
        var endpoint = component.CreateEndpoint(uri);

        Abstractions.IExchange? captured = null;
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => captured = ci.Arg<Abstractions.IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        for (var i = 0; i < 50 && captured == null; i++)
            await Task.Delay(100);

        await consumer.Stop();
        context.Dispose();

        captured.Should().NotBeNull();
        captured!.Properties["CamelTimerName"].Should().Be("heartbeat");
        captured.Properties["CamelTimerGroup"].Should().Be("monitoring");
    }
}
