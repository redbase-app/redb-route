using redb.Route.Core;
using redb.Route.Sqs;

namespace redb.Route.Tests.Sqs;

/// <summary>The component declares the only side it has (<c>[EndpointRole]</c>, Apache Camel's producerOnly / consumerOnly).</summary>
public sealed class ComponentSideTests
{
    [Fact]
    public void The_component_declares_its_only_side()
    {
        EndpointSides.Declaration(typeof(SnsComponent))!.Role.Should().Be(EndpointRole.Producer);
        EndpointSides.Declaration(typeof(SqsComponent)).Should().BeNull("sqs has both sides");
    }
}
