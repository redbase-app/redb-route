using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Волна A6.1 плана KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN. The factory path used to resolve
/// configuration asymmetrically: brokers from the URI were honored, SASL/SSL from the URI were
/// silently swallowed, the endpoint's DEFAULTS for autoOffsetReset/retries stomped the factory's
/// explicit settings. The family rule (TcpConnectionFactory, WsConnectionFactory,
/// HttpConnectionFactory) is one sentence: a parameter the URI actually supplies wins, everything
/// else comes from the factory.
/// </summary>
public sealed class KafkaFactoryPrecedenceTests
{
    private static KafkaEndpointOptions BindOptions(string query, out Dictionary<string, string> supplied)
    {
        supplied = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        var options = new KafkaEndpointOptions();
        options.BindFromUri(supplied);
        return options;
    }

    private static KafkaConnectionFactory Factory() => new()
    {
        Brokers = "factory:9092",
        SecurityProtocol = "SaslSsl",
        SaslMechanism = "ScramSha256",
        SaslUsername = "factory-user",
        SaslPassword = "factory-pass",
        AutoOffsetReset = "Earliest",
        Retries = 7,
        Acks = "All",
    };

    [Fact]
    public void UriSasl_OverridesTheFactory()
    {
        var options = BindOptions("saslUsername=uri-user&saslPassword=uri-pass", out var supplied);
        var config = options.BuildConsumerConfig(Factory(), supplied);

        config.SaslUsername.Should().Be("uri-user",
            "SASL с URI молча съедался фабрикой, хотя brokers с того же URI учитывался");
        config.SaslPassword.Should().Be("uri-pass");
        config.SaslMechanism.Should().Be(Confluent.Kafka.SaslMechanism.ScramSha256,
            "не заданное на URI остаётся фабричным");
    }

    [Fact]
    public void FactoryAutoOffsetReset_SurvivesTheEndpointDefault()
    {
        var options = BindOptions("groupId=g", out var supplied);
        var config = options.BuildConsumerConfig(Factory(), supplied);

        config.AutoOffsetReset.Should().Be(Confluent.Kafka.AutoOffsetReset.Earliest,
            "дефолт эндпоинта (Latest) затирал явную настройку фабрики");
    }

    [Fact]
    public void FactoryRetries_SurviveTheEndpointDefault()
    {
        var options = BindOptions("", out var supplied);
        var config = options.BuildProducerConfig(Factory(), supplied);

        config.MessageSendMaxRetries.Should().Be(7,
            "дефолт эндпоинта (3) затирал явную настройку фабрики");
    }

    [Fact]
    public void UriAcks_OverridesTheFactory()
    {
        var options = BindOptions("acks=leader", out var supplied);
        var config = options.BuildProducerConfig(Factory(), supplied);

        config.Acks.Should().Be(Confluent.Kafka.Acks.Leader,
            "acks с URI на фабричном пути не применялся вовсе");
    }

    [Fact]
    public void FactoryAcks_SurviveWhenUriIsSilent()
    {
        var options = BindOptions("", out var supplied);
        var config = options.BuildProducerConfig(Factory(), supplied);

        config.Acks.Should().Be(Confluent.Kafka.Acks.All);
    }

    // ── Ревью дуги: гибридные конфигурации через компонент ──

    [Fact]
    public void FactorySaslCredentials_SatisfyAUriMechanism()
    {
        // The RequireSaslCredentials error text offers "inline or via a named connectionFactory",
        // but the component used to copy only Brokers from the factory before Validate() - so the
        // second path it advertised was a lie: URI mechanism + factory credentials always threw.
        var ctx = new RouteContext();
        ctx.AddComponent(new KafkaComponent());
        ctx.AddToRegistry("cf", Factory());

        var act = () => ctx.GetEndpoint(
            "kafka://orders?connectionFactory=cf&saslMechanism=ScramSha512&securityProtocol=SaslSsl&groupId=g");
        act.Should().NotThrow("креды фабрики — второй легальный путь из текста ошибки");
    }

    [Fact]
    public void FactoryGroupId_IsUsed_WhenTheUriHasNone()
    {
        // KafkaConnectionFactory.GroupId documented "Default consumer group ID", but nothing ever
        // read it: the endpoint demanded a URI groupId before the factory value could apply -
        // a dead option left behind by the dead-options sweep itself.
        var ctx = new RouteContext();
        ctx.AddComponent(new KafkaComponent());
        var factory = Factory();
        factory.GroupId = "cf-group";
        ctx.AddToRegistry("cf", factory);

        var endpoint = ctx.GetEndpoint("kafka://orders?connectionFactory=cf");
        var act = () => endpoint.CreateConsumer(Substitute.For<IProcessor>());
        act.Should().NotThrow("фабричный GroupId обязан работать как дефолт");
    }
}
