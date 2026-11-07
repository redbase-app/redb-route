using redb.Route.Core;
using redb.Route.Llm.Embeddings;
using redb.Route.Llm.Knowledge;
using redb.Route.Llm.Transcription;

namespace redb.Route.Tests.Llm;

/// <summary>The component declares the only side it has (<c>[EndpointRole]</c>, Apache Camel's producerOnly / consumerOnly).</summary>
public sealed class ComponentSideTests
{
    [Fact]
    public void The_component_declares_its_only_side()
    {
        EndpointSides.Declaration(typeof(EmbedComponent))!.Role.Should().Be(EndpointRole.Producer);
        EndpointSides.Declaration(typeof(KnowledgeComponent))!.Role.Should().Be(EndpointRole.Producer);
        EndpointSides.Declaration(typeof(SttComponent))!.Role.Should().Be(EndpointRole.Producer);
    }
}
