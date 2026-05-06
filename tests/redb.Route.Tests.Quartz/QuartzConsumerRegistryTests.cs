using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// Tests for QuartzConsumerRegistry — the static consumer instance registry
/// that fixes the original's CreateConsumer(null) bug.
/// </summary>
public class QuartzConsumerRegistryTests
{
    [Fact]
    public void Get_Unregistered_ReturnsNull()
    {
        QuartzConsumerRegistry.Get("nonexistent://endpoint").Should().BeNull();
    }

    [Fact]
    public void RegisterAndGet_ReturnsConsumer()
    {
        var key = "registry-test://ep1";
        var consumer = CreateMockConsumer();

        try
        {
            QuartzConsumerRegistry.Register(key, consumer);
            QuartzConsumerRegistry.Get(key).Should().BeSameAs(consumer);
        }
        finally
        {
            QuartzConsumerRegistry.Unregister(key);
        }
    }

    [Fact]
    public void Unregister_RemovesConsumer()
    {
        var key = "registry-test://ep2";
        var consumer = CreateMockConsumer();

        QuartzConsumerRegistry.Register(key, consumer);
        QuartzConsumerRegistry.Unregister(key);
        QuartzConsumerRegistry.Get(key).Should().BeNull();
    }

    [Fact]
    public void Unregister_NonExistent_DoesNotThrow()
    {
        var act = () => QuartzConsumerRegistry.Unregister("does-not-exist://x");
        act.Should().NotThrow();
    }

    [Fact]
    public void Register_OverwritesPrevious()
    {
        var key = "registry-test://ep3";
        var c1 = CreateMockConsumer();
        var c2 = CreateMockConsumer();

        try
        {
            QuartzConsumerRegistry.Register(key, c1);
            QuartzConsumerRegistry.Register(key, c2);
            QuartzConsumerRegistry.Get(key).Should().BeSameAs(c2);
        }
        finally
        {
            QuartzConsumerRegistry.Unregister(key);
        }
    }

    [Fact]
    public void MultipleKeys_Independent()
    {
        var key1 = "registry-multi://a";
        var key2 = "registry-multi://b";
        var c1 = CreateMockConsumer();
        var c2 = CreateMockConsumer();

        try
        {
            QuartzConsumerRegistry.Register(key1, c1);
            QuartzConsumerRegistry.Register(key2, c2);

            QuartzConsumerRegistry.Get(key1).Should().BeSameAs(c1);
            QuartzConsumerRegistry.Get(key2).Should().BeSameAs(c2);

            QuartzConsumerRegistry.Unregister(key1);
            QuartzConsumerRegistry.Get(key1).Should().BeNull();
            QuartzConsumerRegistry.Get(key2).Should().BeSameAs(c2, "unregistering key1 should not affect key2");
        }
        finally
        {
            QuartzConsumerRegistry.Unregister(key1);
            QuartzConsumerRegistry.Unregister(key2);
        }
    }

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
