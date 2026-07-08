using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// Pure-logic tests for the connection-pool key resolution implemented in
/// <see cref="RabbitMQComponent"/>. These do not require a running broker — they verify
/// that the pool's identity model behaves as designed:
/// <list type="bullet">
///   <item><b>factory:{name}</b> when <c>connectionFactory=name</c> is set on the URI.</item>
///   <item><b>inline:{host}:{port}/{vhost}@{user}[#ssl]</b> otherwise.</item>
///   <item>Two factories with identical inline parameters but different names produce
///     two distinct keys (deliberate: factory name is the connection identity).</item>
/// </list>
/// </summary>
public sealed class RabbitMQConnectionPoolTests
{
    private static RabbitMQEndpointOptions OptionsFromUri(string uri)
    {
        var parsed = EndpointUriParser.Parse(uri);
        var opts = new RabbitMQEndpointOptions();
        opts.BindFromUri(parsed.RawParameters);
        opts.Validate();
        return opts;
    }

    [Fact]
    public void ResolveKey_FactoryName_ProducesFactoryKey()
    {
        var opts = OptionsFromUri("rabbitmq://q?connectionFactory=primary");

        RabbitMQComponent.ResolveConnectionKey(opts).Should().Be("factory:primary");
    }

    [Fact]
    public void ResolveKey_NoFactory_ProducesInlineKey()
    {
        var opts = OptionsFromUri("rabbitmq://q?host=broker.example.com&port=5672&virtualHost=/&username=alice");

        RabbitMQComponent.ResolveConnectionKey(opts)
            .Should().Be("inline:broker.example.com:5672//@alice");
    }

    [Fact]
    public void ResolveKey_InlineWithSsl_AppendsSslMarker()
    {
        var opts = OptionsFromUri("rabbitmq://q?host=secure.example.com&port=5671&username=bob&ssl=true");

        RabbitMQComponent.ResolveConnectionKey(opts)
            .Should().Be("inline:secure.example.com:5671//@bob#ssl");
    }

    [Fact]
    public void ResolveKey_TwoEndpointsSameFactoryName_ShareKey()
    {
        var a = OptionsFromUri("rabbitmq://qA?connectionFactory=primary");
        var b = OptionsFromUri("rabbitmq://qB?connectionFactory=primary");

        RabbitMQComponent.ResolveConnectionKey(a)
            .Should().Be(RabbitMQComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void ResolveKey_TwoFactoriesDifferentNamesSameInlineParams_ProduceDistinctKeys()
    {
        // Per design: factory name IS the connection identity. Two named factories with
        // identical broker parameters MUST yield two distinct connections — this lets a
        // single application open N parallel connections to the same broker on purpose.
        var a = OptionsFromUri("rabbitmq://q?connectionFactory=conn1");
        var b = OptionsFromUri("rabbitmq://q?connectionFactory=conn2");

        var keyA = RabbitMQComponent.ResolveConnectionKey(a);
        var keyB = RabbitMQComponent.ResolveConnectionKey(b);

        keyA.Should().Be("factory:conn1");
        keyB.Should().Be("factory:conn2");
        keyA.Should().NotBe(keyB);
    }

    [Fact]
    public void ResolveKey_TwoEndpointsIdenticalInlineParams_ShareKey()
    {
        var a = OptionsFromUri("rabbitmq://qA?host=h&port=5672&virtualHost=/&username=u");
        var b = OptionsFromUri("rabbitmq://qB?host=h&port=5672&virtualHost=/&username=u");

        RabbitMQComponent.ResolveConnectionKey(a)
            .Should().Be(RabbitMQComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void ResolveKey_DifferentInlineHosts_ProduceDistinctKeys()
    {
        var a = OptionsFromUri("rabbitmq://q?host=h1&username=u");
        var b = OptionsFromUri("rabbitmq://q?host=h2&username=u");

        RabbitMQComponent.ResolveConnectionKey(a)
            .Should().NotBe(RabbitMQComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void ResolveKey_DifferentInlineUsers_ProduceDistinctKeys()
    {
        var a = OptionsFromUri("rabbitmq://q?host=h&username=alice");
        var b = OptionsFromUri("rabbitmq://q?host=h&username=bob");

        RabbitMQComponent.ResolveConnectionKey(a)
            .Should().NotBe(RabbitMQComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void ResolveKey_DifferentInlineVHosts_ProduceDistinctKeys()
    {
        var a = OptionsFromUri("rabbitmq://q?host=h&username=u&virtualHost=/prod");
        var b = OptionsFromUri("rabbitmq://q?host=h&username=u&virtualHost=/dev");

        RabbitMQComponent.ResolveConnectionKey(a)
            .Should().NotBe(RabbitMQComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void Component_DisposeAsync_OnEmptyPool_DoesNotThrow()
    {
        var component = new RabbitMQComponent();

        var act = async () => await component.DisposeAsync();

        act.Should().NotThrowAsync();
    }
}
