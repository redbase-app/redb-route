using redb.Route.Core;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>The component declares the only side it has (<c>[EndpointRole]</c>, Apache Camel's producerOnly / consumerOnly).</summary>
public sealed class ComponentSideTests
{
    [Fact]
    public void The_component_declares_its_only_side()
    {
        EndpointSides.Declaration(typeof(CronComponent))!.Role.Should().Be(EndpointRole.Consumer);
        EndpointSides.Declaration(typeof(QuartzTimerComponent))!.Role.Should().Be(EndpointRole.Consumer);
    }
}
