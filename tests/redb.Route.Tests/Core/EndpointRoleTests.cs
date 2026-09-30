using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// An option that only one side of an endpoint reads is declared so on its property (<c>[EndpointRole]</c>, Camel's
/// <c>@UriParam(label = "producer"/"consumer")</c>): the engine refuses it on the other side by the declaration, and
/// tooling reads the same declaration by reflection.
/// </summary>
public class EndpointRoleTests
{
    public sealed class Probe : EndpointOptions
    {
        [EndpointRole(EndpointRole.Producer)]
        public string? Password { get; set; }

        [EndpointRole(EndpointRole.Consumer)]
        public string? InboundRealm { get; set; }

        public int Timeout { get; set; }

        public override void Validate() { }
    }

    private static EndpointUri Uri(string query)
        => EndpointUriParser.Parse("probe://x?" + query);

    [Fact]
    public void The_role_of_an_option_is_read_from_its_declaration()
    {
        EndpointOptions.RoleOf(typeof(Probe).GetProperty(nameof(Probe.Password))!).Should().Be(EndpointRole.Producer);
        EndpointOptions.RoleOf(typeof(Probe).GetProperty(nameof(Probe.InboundRealm))!).Should().Be(EndpointRole.Consumer);
        EndpointOptions.RoleOf(typeof(Probe).GetProperty(nameof(Probe.Timeout))!).Should().BeNull("an option both sides read has no role");
    }

    [Fact]
    public void A_consumer_finds_the_producer_options_its_uri_wrote_as_written()
    {
        var written = EndpointOptions.WrittenForOtherRole(typeof(Probe), Uri("PASSWORD=x&timeout=5&inboundRealm=r"), EndpointRole.Consumer);

        written.Should().Equal("PASSWORD");
    }

    [Fact]
    public void A_producer_finds_the_consumer_options_its_uri_wrote()
    {
        var written = EndpointOptions.WrittenForOtherRole(typeof(Probe), Uri("password=x&inboundRealm=r"), EndpointRole.Producer);

        written.Should().Equal("inboundRealm");
    }
}
