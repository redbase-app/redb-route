using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Ф11 волна Ж (Ж-1): a set-but-unknown <c>connectionFactory</c> name must fail loud —
/// never silently fall back to URI parameters.
/// </summary>
public sealed class KafkaConnectionFactoryLoudTests
{
    [Fact]
    public async Task ConnectionFactoryTypo_FailsLoud()
    {
        await using var ctx = new RouteContext();
        ctx.AddComponent(new KafkaComponent());

        var act = () => ctx.GetEndpoint("kafka://orders?connectionFactory=typo-name&brokers=localhost:9092");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("typo-name");
    }

    [Fact]
    public async Task ConnectionFactoryRegistered_Resolves()
    {
        await using var ctx = new RouteContext();
        ctx.AddComponent(new KafkaComponent());
        ctx.AddToRegistry("real", new KafkaConnectionFactory { Brokers = "broker:9092" });

        var endpoint = ctx.GetEndpoint("kafka://orders?connectionFactory=real");

        endpoint.Should().NotBeNull();
    }
}
