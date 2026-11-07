using redb.Route.Core;
using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// Pure-logic tests for the IBM MQ connection-pool key resolution implemented in
/// <see cref="IbmMqComponent"/>. Same identity model as RabbitMQ and AMQP: factory-name
/// keyed, inline auto-deduplicated.
/// </summary>
public sealed class IbmMqConnectionPoolTests
{
    private static IbmMqEndpointOptions OptionsFromUri(string uri)
    {
        var parsed = EndpointUriParser.Parse(uri);
        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(parsed.RawParameters);
        opts.Validate();
        return opts;
    }

    [Fact]
    public void ResolveKey_FactoryName_ProducesFactoryKey()
    {
        var opts = OptionsFromUri("wmq:Q.IN?host=h&queueManager=QM1&connectionFactory=primary");
        IbmMqComponent.ResolveConnectionKey(opts).Should().Be("factory:primary");
    }

    [Fact]
    public void ResolveKey_NoFactory_ProducesInlineKey()
    {
        var opts = OptionsFromUri(
            "wmq:Q.IN?host=mq&port=1414&queueManager=QM1&channel=DEV.APP.SVRCONN&user=alice");

        var key = IbmMqComponent.ResolveConnectionKey(opts);

        key.Should().Be("inline:mq:1414/QM1#DEV.APP.SVRCONN@alice");
    }

    [Fact]
    public void ResolveKey_TwoFactoriesDifferentNamesSameInlineParams_ProduceDistinctKeys()
    {
        var a = OptionsFromUri("wmq:Q?host=h&queueManager=QM1&connectionFactory=conn1");
        var b = OptionsFromUri("wmq:Q?host=h&queueManager=QM1&connectionFactory=conn2");

        IbmMqComponent.ResolveConnectionKey(a).Should().Be("factory:conn1");
        IbmMqComponent.ResolveConnectionKey(b).Should().Be("factory:conn2");
    }

    [Fact]
    public void ResolveKey_TwoEndpointsIdenticalInlineParams_ShareKey()
    {
        var a = OptionsFromUri("wmq:Q.A?host=mq&port=1414&queueManager=QM1&channel=CH1&user=u");
        var b = OptionsFromUri("wmq:Q.B?host=mq&port=1414&queueManager=QM1&channel=CH1&user=u");

        IbmMqComponent.ResolveConnectionKey(a)
            .Should().Be(IbmMqComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void ResolveKey_DifferentQueueManagers_ProduceDistinctKeys()
    {
        var a = OptionsFromUri("wmq:Q?host=mq&queueManager=QM1");
        var b = OptionsFromUri("wmq:Q?host=mq&queueManager=QM2");

        IbmMqComponent.ResolveConnectionKey(a)
            .Should().NotBe(IbmMqComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void ResolveKey_DifferentChannels_ProduceDistinctKeys()
    {
        var a = OptionsFromUri("wmq:Q?host=mq&queueManager=QM1&channel=CH.A");
        var b = OptionsFromUri("wmq:Q?host=mq&queueManager=QM1&channel=CH.B");

        IbmMqComponent.ResolveConnectionKey(a)
            .Should().NotBe(IbmMqComponent.ResolveConnectionKey(b));
    }

    [Fact]
    public void ResolveKey_InlineWithSslCipherSpec_AppendsSslMarker()
    {
        var opts = OptionsFromUri(
            "wmq:Q?host=mq&queueManager=QM1&user=u&sslCipherSpec=TLS_RSA_WITH_AES_256_CBC_SHA256");

        IbmMqComponent.ResolveConnectionKey(opts).Should().EndWith("#ssl");
    }

    [Fact]
    public async Task Component_DisposeAsync_OnEmptyPool_DoesNotThrow()
    {
        var component = new IbmMqComponent();
        var act = async () => await component.DisposeAsync();
        await act.Should().NotThrowAsync();
    }
}
