using redb.Route.Amqp;
using redb.Route.Core;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// Pure-logic tests for the AMQP connection-pool key resolution implemented in
/// <see cref="AmqpComponent"/>. Same identity model as RabbitMQ: factory-name keyed,
/// inline auto-deduplicated.
/// </summary>
public sealed class AmqpConnectionPoolTests
{
    private static AmqpEndpointOptions OptionsFromUri(string uri)
    {
        var parsed = EndpointUriParser.Parse(uri);
        var opts = new AmqpEndpointOptions();
        opts.BindFromUri(parsed.RawParameters);
        opts.Validate();
        return opts;
    }

    [Fact]
    public void ResolveKey_FactoryName_ProducesFactoryKey()
    {
        var opts = OptionsFromUri("amqp://addr?host=h&connectionFactory=primary");
        AmqpComponent.ResolveConnectionKey(opts).Should().Be("factory:primary");
    }

    [Fact]
    public void ResolveKey_NoFactory_ProducesInlineKey()
    {
        var opts = OptionsFromUri("amqp://addr?host=broker&port=5672&user=alice");
        var key = AmqpComponent.ResolveConnectionKey(opts);
        key.Should().StartWith("inline:broker:5672/").And.EndWith("@alice");
    }

    [Fact]
    public void ResolveKey_TwoFactoriesDifferentNamesSameInlineParams_ProduceDistinctKeys()
    {
        var a = OptionsFromUri("amqp://q?host=h&connectionFactory=conn1");
        var b = OptionsFromUri("amqp://q?host=h&connectionFactory=conn2");

        AmqpComponent.ResolveConnectionKey(a).Should().Be("factory:conn1");
        AmqpComponent.ResolveConnectionKey(b).Should().Be("factory:conn2");
    }

    [Fact]
    public void ResolveKey_TwoEndpointsIdenticalInlineParams_ShareKey()
    {
        var a = OptionsFromUri("amqp://qA?host=h&port=5672&user=u");
        var b = OptionsFromUri("amqp://qB?host=h&port=5672&user=u");

        AmqpComponent.ResolveConnectionKey(a)
            .Should().Be(AmqpComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void ResolveKey_DifferentInlineHosts_ProduceDistinctKeys()
    {
        var a = OptionsFromUri("amqp://q?host=h1&user=u");
        var b = OptionsFromUri("amqp://q?host=h2&user=u");

        AmqpComponent.ResolveConnectionKey(a)
            .Should().NotBe(AmqpComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void ResolveKey_InlineWithSsl_AppendsSslMarker()
    {
        var opts = OptionsFromUri("amqp://addr?host=secure&port=5671&user=u&ssl=true");
        AmqpComponent.ResolveConnectionKey(opts).Should().EndWith("#ssl");
    }

    /// <summary>
    /// connectionFactory= takes the whole connection from the registry factory: host, port, credentials and TLS. The
    /// address used to come from the URI options, so a factory's broker other than localhost:5672 was never reached.
    /// </summary>
    [Fact]
    public async Task A_named_factory_gives_the_connection_its_host_port_credentials_and_tls()
    {
        var component = new AmqpComponent();
        await using var context = new RouteContext();
        context.AddComponent(component);
        context.AddToRegistry("broker", new AmqpConnectionFactory
        {
            Host = "artemis.internal, artemis-2.internal", Port = 5999, User = "svc", Password = "pw", Ssl = true,
        });

        var settings = component.ResolveConnectionSettings(OptionsFromUri("amqp://orders?connectionFactory=broker"));
        var address = AmqpComponent.ConnectionAddress(settings);

        address.Host.Should().Be("artemis.internal");
        address.Port.Should().Be(5999);
        address.User.Should().Be("svc");
        address.Password.Should().Be("pw");
        address.Scheme.Should().Be("amqps");
    }

    [Fact]
    public async Task Without_a_factory_the_connection_takes_the_URI_parameters()
    {
        var component = new AmqpComponent();
        await using var context = new RouteContext();
        context.AddComponent(component);

        var settings = component.ResolveConnectionSettings(
            OptionsFromUri("amqp://orders?host=broker-a,broker-b&port=5673&user=alice&password=secret"));
        var address = AmqpComponent.ConnectionAddress(settings);

        address.Host.Should().Be("broker-a");
        address.Port.Should().Be(5673);
        address.User.Should().Be("alice");
        address.Scheme.Should().Be("amqp");
    }

    [Fact]
    public async Task An_unknown_factory_name_fails_instead_of_using_the_URI()
    {
        var component = new AmqpComponent();
        await using var context = new RouteContext();
        context.AddComponent(component);

        var resolve = () => component.ResolveConnectionSettings(OptionsFromUri("amqp://orders?connectionFactory=missing"));

        resolve.Should().Throw<Exception>().WithMessage("*missing*");
    }

    [Fact]
    public void Component_DisposeAsync_OnEmptyPool_DoesNotThrow()
    {
        var component = new AmqpComponent();
        var act = async () => await component.DisposeAsync();
        act.Should().NotThrowAsync();
    }
}
