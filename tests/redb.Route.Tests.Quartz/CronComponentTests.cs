using redb.Route.Core;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// Tests for CronComponent, CronEndpoint, CronEndpointOptions, and CronConsumer.
/// </summary>
public class CronComponentTests
{
    // ── Component ──

    [Fact]
    public void Scheme_IsCron()
    {
        var component = new CronComponent();
        component.Scheme.Should().Be("cron");
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new CronComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsCronEndpoint()
    {
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://myJob?schedule=0/5 * * * * ?");
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<CronEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_MissingSchedule_Throws()
    {
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://myJob");
        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*schedule*");
    }

    [Fact]
    public void CreateEndpoint_InvalidCronExpression_Throws()
    {
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://myJob?schedule=not-a-cron");
        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*Invalid cron*");
    }

    // ── Options ──

    [Fact]
    public void Options_DefaultValues()
    {
        var opts = new CronEndpointOptions();
        opts.Schedule.Should().BeEmpty();
        opts.Threads.Should().Be(1);
    }

    [Fact]
    public void Options_Validate_MissingSchedule_Throws()
    {
        var opts = new CronEndpointOptions();
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*schedule*");
    }

    [Fact]
    public void Options_Validate_InvalidSchedule_Throws()
    {
        var opts = new CronEndpointOptions { Schedule = "invalid" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Invalid cron*");
    }

    [Fact]
    public void Options_Validate_ZeroThreads_Throws()
    {
        var opts = new CronEndpointOptions { Schedule = "0/5 * * * * ?", Threads = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Validate_ValidSchedule_Passes()
    {
        var opts = new CronEndpointOptions { Schedule = "0 0 12 * * ?" };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Options_BindFromUri_ParsesScheduleAndThreads()
    {
        var uri = EndpointUriParser.Parse("cron://job1?schedule=0/10 * * * * ?&threads=4");
        var opts = new CronEndpointOptions();
        opts.BindFromUri(uri.RawParameters);
        opts.Schedule.Should().Be("0/10 * * * * ?");
        opts.Threads.Should().Be(4);
    }

    // ── Endpoint ──

    [Fact]
    public void Endpoint_CreateProducer_ThrowsNotSupported()
    {
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://myJob?schedule=0/5 * * * * ?");
        var endpoint = component.CreateEndpoint(uri);
        var act = () => endpoint.CreateProducer();
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Endpoint_CreateConsumer_NullProcessor_Throws()
    {
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://myJob?schedule=0/5 * * * * ?");
        var endpoint = component.CreateEndpoint(uri);
        var act = () => endpoint.CreateConsumer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Endpoint_CreateConsumer_ReturnsCronConsumer()
    {
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://myJob?schedule=0/5 * * * * ?");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<Abstractions.IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);
        consumer.Should().BeOfType<CronConsumer>();
    }

    // ── Consumer lifecycle with real Quartz scheduler ──

    [Fact]
    public async Task Consumer_StartStop_Lifecycle()
    {
        var component = new CronComponent();
        var context = new RouteContext("cron-test-1");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("cron://tick?schedule=0/1 * * * * ?");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = endpoint.CreateConsumer(processor);

        // Start should not throw
        await consumer.Start();

        // Consumer should be registered
        QuartzConsumerRegistry.Get(endpoint.Uri.NormalizedKey).Should().NotBeNull();

        // Stop should not throw
        await consumer.Stop();

        // Consumer should be unregistered
        QuartzConsumerRegistry.Get(endpoint.Uri.NormalizedKey).Should().BeNull();

        context.Dispose();
    }

    [Fact]
    public async Task Consumer_RequiresRouteContext()
    {
        // Component without Context → Start should throw
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://tick?schedule=0/1 * * * * ?");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<Abstractions.IProcessor>();

        var consumer = endpoint.CreateConsumer(processor);
        var act = () => consumer.Start();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*RouteContext*");
    }

    [Fact]
    public async Task Consumer_FiresProcessor_OnCronSchedule()
    {
        var component = new CronComponent();
        var context = new RouteContext("cron-fire-test");
        context.AddComponent(component);

        // Every second
        var uri = EndpointUriParser.Parse("cron://rapid?schedule=* * * * * ?");
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

        // Wait up to 5 seconds for at least one fire
        for (var i = 0; i < 50 && fireCount == 0; i++)
            await Task.Delay(100);

        await consumer.Stop();
        context.Dispose();

        fireCount.Should().BeGreaterThan(0, "cron should have fired at least once");
        capturedExchange.Should().NotBeNull();
        capturedExchange!.Properties.Should().ContainKey("CamelCronSchedule");
        capturedExchange.Properties["CamelCronSchedule"].Should().Be("* * * * * ?");
        capturedExchange.Properties.Should().ContainKey("CamelCronName");
        capturedExchange.Properties["CamelCronName"].Should().Be("rapid");
        capturedExchange.Properties.Should().ContainKey("CamelQuartzFireTime");
        capturedExchange.Properties.Should().ContainKey("CamelQuartzScheduledFireTime");
    }

    [Fact]
    public async Task Consumer_ExchangePattern_IsInOnly()
    {
        var component = new CronComponent();
        var context = new RouteContext("cron-pattern-test");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("cron://check?schedule=* * * * * ?");
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
    public async Task Consumer_SharedScheduler_BetweenMultipleConsumers()
    {
        var component = new CronComponent();
        var context = new RouteContext("cron-shared-scheduler");
        context.AddComponent(component);

        var uri1 = EndpointUriParser.Parse("cron://job1?schedule=* * * * * ?");
        var uri2 = EndpointUriParser.Parse("cron://job2?schedule=* * * * * ?");
        var ep1 = component.CreateEndpoint(uri1);
        var ep2 = component.CreateEndpoint(uri2);

        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var c1 = ep1.CreateConsumer(processor);
        var c2 = ep2.CreateConsumer(processor);

        await c1.Start();
        await c2.Start();

        // Both should share the same IScheduler via RouteContext service
        var scheduler = context.GetService<global::Quartz.IScheduler>();
        scheduler.Should().NotBeNull("scheduler should be registered in context");

        await c1.Stop();
        await c2.Stop();
        context.Dispose();
    }

    [Fact]
    public async Task Consumer_StopIdempotent()
    {
        var component = new CronComponent();
        var context = new RouteContext("cron-stop-idem");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("cron://idem?schedule=* * * * * ?");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        // Double stop should not throw
        await consumer.Stop();
        var act = () => consumer.Stop();
        await act.Should().NotThrowAsync();

        context.Dispose();
    }

    [Fact]
    public async Task Consumer_ExceptionInProcessor_HandledViaRouteContext()
    {
        var component = new CronComponent();
        var context = new RouteContext("cron-error-test");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("cron://errJob?schedule=* * * * * ?");
        var endpoint = component.CreateEndpoint(uri);

        var errorThrown = false;
        var processor = Substitute.For<Abstractions.IProcessor>();
        processor.Process(Arg.Any<Abstractions.IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                errorThrown = true;
                throw new InvalidOperationException("Test error in cron processor");
            });

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        // Wait for at least one fire
        for (var i = 0; i < 50 && !errorThrown; i++)
            await Task.Delay(100);

        await consumer.Stop();
        context.Dispose();

        errorThrown.Should().BeTrue("processor should have been called");
        // Exception is handled by routeContext.HandleException — no unhandled crash
    }

    // ── Cron expression validation variants ──

    [Theory]
    [InlineData("0 0 12 * * ?", "every day at noon")]
    [InlineData("0 15 10 ? * MON-FRI", "weekdays at 10:15")]
    [InlineData("0/5 * * * * ?", "every 5 seconds")]
    [InlineData("0 0 0 1 1 ?", "midnight Jan 1")]
    public void Options_Validate_VariousCronExpressions_Passes(string expression, string _)
    {
        var opts = new CronEndpointOptions { Schedule = expression };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    // ── New Camel-compatible options ──

    [Fact]
    public void Options_DefaultGroupName_IsRedb()
    {
        var opts = new CronEndpointOptions();
        opts.GroupName.Should().Be("redb");
    }

    [Fact]
    public void Options_DefaultDeleteJob_IsTrue()
    {
        var opts = new CronEndpointOptions();
        opts.DeleteJob.Should().BeTrue();
    }

    [Fact]
    public void Options_DefaultTriggerStartDelay_Is500()
    {
        var opts = new CronEndpointOptions();
        opts.TriggerStartDelay.Should().Be(500);
    }

    [Fact]
    public void Options_DeleteJobAndPauseJob_BothTrue_Throws()
    {
        var opts = new CronEndpointOptions
        {
            Schedule = "0/5 * * * * ?",
            DeleteJob = true,
            PauseJob = true
        };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*deleteJob*pauseJob*");
    }

    [Fact]
    public void Options_BindFromUri_ParsesNewParams()
    {
        var uri = EndpointUriParser.Parse("cron://job1?schedule=0/10 * * * * ?&stateful=true&recoverableJob=true&timeZone=Europe/Moscow&misfireInstruction=CronDoNothing");
        var opts = new CronEndpointOptions();
        opts.BindFromUri(uri.RawParameters);
        opts.Schedule.Should().Be("0/10 * * * * ?");
        opts.Stateful.Should().BeTrue();
        opts.RecoverableJob.Should().BeTrue();
        opts.TimeZone.Should().Be("Europe/Moscow");
        opts.MisfireInstruction.Should().Be(QuartzMisfirePolicy.CronDoNothing);
    }

    [Fact]
    public void CreateEndpoint_WithGroupInPath_ParsesGroupAndJob()
    {
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://billing/invoice?schedule=0 0 2 * * ?");
        var endpoint = (CronEndpoint)component.CreateEndpoint(uri);
        endpoint.GroupName.Should().Be("billing");
        endpoint.JobName.Should().Be("invoice");
    }

    [Fact]
    public void CreateEndpoint_SingleName_DefaultGroup()
    {
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://myJob?schedule=0/5 * * * * ?");
        var endpoint = (CronEndpoint)component.CreateEndpoint(uri);
        endpoint.GroupName.Should().Be("redb");
        endpoint.JobName.Should().Be("myJob");
    }

    [Fact]
    public void CreateEndpoint_Stateful_PropagatedToEndpoint()
    {
        var component = new CronComponent();
        var uri = EndpointUriParser.Parse("cron://myJob?schedule=0/5 * * * * ?&stateful=true&recoverableJob=true");
        var endpoint = (CronEndpoint)component.CreateEndpoint(uri);
        endpoint.Stateful.Should().BeTrue();
        endpoint.RecoverableJob.Should().BeTrue();
    }

    [Fact]
    public async Task Consumer_FiresWithGroupProperties()
    {
        var component = new CronComponent();
        var context = new RouteContext("cron-group-test");
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("cron://billing/invoice?schedule=* * * * * ?");
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
        captured!.Properties["CamelCronName"].Should().Be("invoice");
        captured.Properties["CamelCronGroup"].Should().Be("billing");
    }
}
