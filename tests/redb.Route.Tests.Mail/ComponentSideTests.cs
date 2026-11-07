using redb.Route.Core;
using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

/// <summary>The component declares the only side it has (<c>[EndpointRole]</c>, Apache Camel's producerOnly / consumerOnly).</summary>
public sealed class ComponentSideTests
{
    [Fact]
    public void The_component_declares_its_only_side()
    {
        EndpointSides.Declaration(typeof(SmtpComponent))!.Role.Should().Be(EndpointRole.Producer);
        EndpointSides.Declaration(typeof(ImapComponent))!.Role.Should().Be(EndpointRole.Consumer);
        EndpointSides.Declaration(typeof(Pop3Component))!.Role.Should().Be(EndpointRole.Consumer);
    }
}
