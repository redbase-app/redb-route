using redb.Route.Core;
using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

/// <summary>The component declares the only side it has (<c>[EndpointRole]</c>, Apache Camel's producerOnly / consumerOnly).</summary>
public sealed class ComponentSideTests
{
    [Fact]
    public void The_component_declares_its_only_side()
    {
        var fcm = typeof(FirebaseStorageComponent).Assembly.GetType("redb.Route.Firebase.FcmComponent", throwOnError: true)!;
        EndpointSides.Declaration(fcm)!.Role.Should().Be(EndpointRole.Producer);
        EndpointSides.Declaration(typeof(FirebaseStorageComponent)).Should().BeNull("fbstorage has both sides");
    }
}
