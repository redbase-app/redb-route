using redb.Route.Core;
using redb.Route.Cache;

namespace redb.Route.Tests.Cache;

/// <summary>The component declares the only side it has (<c>[EndpointRole]</c>, Apache Camel's producerOnly / consumerOnly).</summary>
public sealed class ComponentSideTests
{
    [Fact]
    public void The_component_declares_its_only_side()
    {
        EndpointSides.Declaration(typeof(CacheComponent))!.Role.Should().Be(EndpointRole.Producer);
    }
}
