using FluentAssertions;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// TLS options carry the names most connectors use: sslCertPassword. The former name is refused with the new one named, not
/// bound silently.
/// </summary>
public class RenamedTlsOptionTests
{
    [Fact]
    public void The_former_name_is_refused_with_the_new_one()
    {
        var act = () => new RabbitMQEndpointOptions().BindFromUri(new Dictionary<string, string> { ["sslCertPassphrase"] = "secret" });

        act.Should().Throw<ArgumentException>().WithMessage("*'sslCertPassphrase' is renamed 'sslCertPassword'*");
    }

    [Fact]
    public void The_new_name_binds()
        => new RabbitMQEndpointOptions().BindFromUri(new Dictionary<string, string> { ["sslCertPassword"] = "secret" });
}
