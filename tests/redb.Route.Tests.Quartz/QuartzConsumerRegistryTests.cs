using System.Collections.Specialized;
using Quartz;
using Quartz.Impl;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// Tests for QuartzConsumerRegistry — the running consumers of the process by the job they claimed on their scheduler.
/// One job key, one consumer per scheduler: a second claimant is refused, never silently put in the first one's place.
/// </summary>
public class QuartzConsumerRegistryTests
{
    [Fact]
    public async Task Get_Unregistered_ReturnsNull()
    {
        var scheduler = await NewScheduler();
        QuartzConsumerRegistry.Get(scheduler, new JobKey("nonexistent", "redb")).Should().BeNull();
    }

    [Fact]
    public async Task RegisterAndGet_ReturnsConsumer()
    {
        var scheduler = await NewScheduler();
        var jobKey = new JobKey("ep1", "redb");
        var key = QuartzConsumerRegistry.KeyOf(scheduler, jobKey);
        var consumer = CreateMockConsumer();

        try
        {
            QuartzConsumerRegistry.TryRegister(key, consumer, out _).Should().BeTrue();
            QuartzConsumerRegistry.Get(scheduler, jobKey).Should().BeSameAs(consumer);
        }
        finally
        {
            QuartzConsumerRegistry.Unregister(key, consumer);
        }
    }

    [Fact]
    public async Task Unregister_RemovesConsumer()
    {
        var scheduler = await NewScheduler();
        var jobKey = new JobKey("ep2", "redb");
        var key = QuartzConsumerRegistry.KeyOf(scheduler, jobKey);
        var consumer = CreateMockConsumer();

        QuartzConsumerRegistry.TryRegister(key, consumer, out _);
        QuartzConsumerRegistry.Unregister(key, consumer);
        QuartzConsumerRegistry.Get(scheduler, jobKey).Should().BeNull();
    }

    [Fact]
    public void Unregister_NonExistent_DoesNotThrow()
    {
        var act = () => QuartzConsumerRegistry.Unregister("does-not-exist", CreateMockConsumer());
        act.Should().NotThrow();
    }

    [Fact]
    public async Task ASecondClaimant_IsRefused_AndTheFirstStays()
    {
        var scheduler = await NewScheduler();
        var jobKey = new JobKey("ep3", "redb");
        var key = QuartzConsumerRegistry.KeyOf(scheduler, jobKey);
        var first = CreateMockConsumer();
        var second = CreateMockConsumer();

        try
        {
            QuartzConsumerRegistry.TryRegister(key, first, out _).Should().BeTrue();
            QuartzConsumerRegistry.TryRegister(key, second, out var owner).Should().BeFalse();
            owner.Should().BeSameAs(first);

            // The refused one cannot release what it does not hold.
            QuartzConsumerRegistry.Unregister(key, second);
            QuartzConsumerRegistry.Get(scheduler, jobKey).Should().BeSameAs(first);
        }
        finally
        {
            QuartzConsumerRegistry.Unregister(key, first);
        }
    }

    [Fact]
    public async Task TheSameJobKey_OnAnotherScheduler_IsAnotherClaim()
    {
        var a = await NewScheduler();
        var b = await NewScheduler();
        var jobKey = new JobKey("ep4", "redb");
        var c1 = CreateMockConsumer();
        var c2 = CreateMockConsumer();

        try
        {
            QuartzConsumerRegistry.TryRegister(QuartzConsumerRegistry.KeyOf(a, jobKey), c1, out _).Should().BeTrue();
            QuartzConsumerRegistry.TryRegister(QuartzConsumerRegistry.KeyOf(b, jobKey), c2, out _).Should().BeTrue(
                "a scheduler per context: two contexts may both say cron://ep4");

            QuartzConsumerRegistry.Get(a, jobKey).Should().BeSameAs(c1);
            QuartzConsumerRegistry.Get(b, jobKey).Should().BeSameAs(c2);
        }
        finally
        {
            QuartzConsumerRegistry.Unregister(QuartzConsumerRegistry.KeyOf(a, jobKey), c1);
            QuartzConsumerRegistry.Unregister(QuartzConsumerRegistry.KeyOf(b, jobKey), c2);
        }
    }

    private static Task<IScheduler> NewScheduler()
        => new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "registry-test-" + Guid.NewGuid().ToString("N"),
        }).GetScheduler();

    private static QuartzConsumerBase CreateMockConsumer()
    {
        return Substitute.ForPartsOf<QuartzConsumerBase>(
            Substitute.For<Abstractions.IEndpoint>(),
            Substitute.For<Abstractions.IProcessor>(),
            1,              // maxThreads
            (string?)null,  // groupName
            (string?)null,  // jobName
            true,           // deleteJob
            false,          // pauseJob
            false,          // durableJob
            false,          // stateful
            false,          // recoverableJob
            false,          // prefixJobNameWithEndpointId
            (string?)null); // customCalendar
    }
}
