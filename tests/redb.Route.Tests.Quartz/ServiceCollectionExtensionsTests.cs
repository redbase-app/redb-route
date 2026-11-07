using Microsoft.Extensions.DependencyInjection;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// Tests for ServiceCollectionExtensions — DI registration methods.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteQuartz_RegistersBothComponents()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteQuartz();

        var provider = services.BuildServiceProvider();

        provider.GetService<CronComponent>().Should().NotBeNull();
        provider.GetService<QuartzTimerComponent>().Should().NotBeNull();
    }

    [Fact]
    public void AddRedbRouteCron_RegistersCronOnly()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteCron();

        var provider = services.BuildServiceProvider();

        provider.GetService<CronComponent>().Should().NotBeNull();
        provider.GetService<QuartzTimerComponent>().Should().BeNull();
    }

    [Fact]
    public void AddRedbRouteQuartzTimer_RegistersTimerOnly()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteQuartzTimer();

        var provider = services.BuildServiceProvider();

        provider.GetService<QuartzTimerComponent>().Should().NotBeNull("AddRedbRouteQuartzTimer should register QuartzTimerComponent");
        provider.GetService<CronComponent>().Should().BeNull("AddRedbRouteQuartzTimer should NOT register CronComponent");
    }

    [Fact]
    public void AddRedbRouteQuartz_ComponentsSingleton()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteQuartz();

        var provider = services.BuildServiceProvider();

        var cron1 = provider.GetService<CronComponent>();
        var cron2 = provider.GetService<CronComponent>();
        cron1.Should().BeSameAs(cron2, "CronComponent should be singleton");

        var timer1 = provider.GetService<QuartzTimerComponent>();
        var timer2 = provider.GetService<QuartzTimerComponent>();
        timer1.Should().BeSameAs(timer2, "QuartzTimerComponent should be singleton");
    }
}
