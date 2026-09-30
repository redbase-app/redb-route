using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// A named connection factory is the whole connection, as in Camel ("all connection options set on URI are not
/// used"). An option a factory sets is declared so (<c>[ConnectionParameter]</c>), the option naming the factory is
/// declared too (<c>[ConnectionFactoryReference]</c>), and the core refuses the first beside the second for every
/// connector, when the endpoint's options are bound — naming the parameters, never their values.
/// </summary>
public class ConnectionParameterTests
{
    public sealed class Probe : EndpointOptions
    {
        [ConnectionFactoryReference]
        public string? ConnectionFactory { get; set; }

        [ConnectionParameter]
        public string? Host { get; set; }

        [ConnectionParameter, Sensitive]
        public string? Password { get; set; }

        public string? Exchange { get; set; }

        public override void Validate() { }
    }

    public sealed class Misdeclared : EndpointOptions
    {
        [ConnectionParameter]
        public string? Host { get; set; }

        public override void Validate() { }
    }

    private static void Bind(EndpointOptions options, string query)
        => options.BindFromUri(EndpointUriParser.Parse("probe://x?" + query).RawParameters);

    [Fact]
    public void A_connection_parameter_beside_the_factory_is_refused_by_name_only()
    {
        var act = () => Bind(new Probe(), "connectionFactory=prod&HOST=broker-2&password=s3cret");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*'prod'*HOST*password*")
            .Which.Message.Should().NotContain("s3cret").And.NotContain("broker-2");
    }

    [Fact]
    public void Endpoint_options_beside_the_factory_are_accepted()
        => Bind(new Probe(), "connectionFactory=prod&exchange=orders");

    [Fact]
    public void Connection_parameters_without_a_factory_are_accepted()
        => Bind(new Probe(), "host=broker-2&password=x");

    [Fact]
    public void The_parameters_are_found_as_written()
        => EndpointOptions.WrittenBesideConnectionFactory(typeof(Probe),
                EndpointUriParser.Parse("probe://x?connectionFactory=prod&HOST=h&exchange=e"))
            .Should().Equal("HOST");

    [Fact]
    public void Connection_parameters_without_a_declared_factory_option_are_a_declaration_error()
    {
        var act = () => Bind(new Misdeclared(), "host=h");

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionFactoryReference*");
    }
}
