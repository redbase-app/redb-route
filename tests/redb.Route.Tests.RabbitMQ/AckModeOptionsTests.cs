using FluentAssertions;
using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// ackMode, the one option that says when a consumer settles what it received (manual: after the route,
/// auto: on receipt). The connector's former option is refused with its replacement named.
/// </summary>
public class AckModeOptionsTests
{
    private static RabbitMQEndpointOptions Bind(params (string Key, string Value)[] parameters)
    {
        var options = new RabbitMQEndpointOptions();
        options.BindFromUri(parameters.ToDictionary(p => p.Key, p => p.Value));
        return options;
    }

    [Fact]
    public void The_default_is_manual() => new RabbitMQEndpointOptions().AckMode.Should().Be(AckMode.Manual);

    [Fact]
    public void Auto_binds_from_the_uri() => Bind(("ackMode", "auto")).AckMode.Should().Be(AckMode.Auto);

    [Fact]
    public void The_former_option_is_refused_with_its_replacement()
    {
        var act = () => Bind(("autoAck", "true"));

        act.Should().Throw<ArgumentException>().WithMessage("*'autoAck' is replaced by 'ackMode'*");
    }

}
