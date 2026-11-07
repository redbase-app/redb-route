using redb.Route.Core;
using redb.Route.Llm.Mcp;

namespace redb.Route.Tests.Llm.Mcp;

/// <summary>The component declares the only side it has (<c>[EndpointRole]</c>, Apache Camel's producerOnly / consumerOnly).</summary>
public sealed class ComponentSideTests
{
    [Fact]
    public void The_component_declares_its_only_side()
    {
        EndpointSides.Declaration(typeof(McpComponent))!.Role.Should().Be(EndpointRole.Producer);
    }
}
