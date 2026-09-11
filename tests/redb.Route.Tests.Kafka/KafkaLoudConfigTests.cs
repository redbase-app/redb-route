using Confluent.Kafka;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Волна A1 плана KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN: a typo in an enum-valued option must
/// fail endpoint creation, not silently fall back to a default. The worst case was security:
/// <c>securityProtocol=SaslSSL</c> (typo) used to yield a PLAINTEXT connection with no
/// credentials, because both TryParse branches quietly skipped the assignment. The rest are the
/// same disease with milder symptoms: acks=quorum silently became Leader, isolationLevel typo
/// silently read uncommitted, autoOffsetReset typo silently skipped history.
/// </summary>
public sealed class KafkaLoudConfigTests
{
    private static KafkaEndpoint CreateEndpoint(string parameters)
    {
        var uri = EndpointUriParser.Parse($"kafka://orders?brokers=localhost:9092&{parameters}");
        return (KafkaEndpoint)new KafkaComponent().CreateEndpoint(uri);
    }

    // ── A typo must fail ──

    [Theory]
    [InlineData("securityProtocol=SaslSSLx")]
    [InlineData("securityProtocol=SASLL_SSL")]
    [InlineData("saslMechanism=Plane&saslUsername=u&saslPassword=p&securityProtocol=SaslSsl")]
    [InlineData("acks=quorum")]
    [InlineData("isolationLevel=readcommited")]
    [InlineData("autoOffsetReset=earlist")]
    [InlineData("compressionType=gzipp")]
    [InlineData("partitionAssignmentStrategy=sticky")]
    [InlineData("seekTo=start")]
    public void Typo_FailsEndpointCreation_NamingTheOption(string parameters)
    {
        var act = () => CreateEndpoint(parameters);

        var optionName = parameters.Split('=')[0];
        act.Should().Throw<ArgumentException>(
                $"опечатка в '{optionName}' молча давала значение по умолчанию")
            .WithMessage($"*{optionName}*", "ошибка обязана называть опцию");
    }

    [Fact]
    public void SecurityProtocolTypo_MessageListsTheValidValues()
    {
        var act = () => CreateEndpoint("securityProtocol=SaslSSLx");
        act.Should().Throw<ArgumentException>().WithMessage("*SaslSsl*",
            "админ должен увидеть допустимые значения, а не идти в исходники");
    }

    [Fact]
    public void FactoryWithTypo_FailsOnBuild()
    {
        var factory = new KafkaConnectionFactory { SecurityProtocol = "SaslSSLx" };
        var act = () => factory.BuildConsumerConfig("g");
        act.Should().Throw<ArgumentException>().WithMessage("*securityProtocol*");
    }

    // ── Legitimate forms must work, including the canonical Kafka spellings ──

    [Theory]
    [InlineData("SaslSsl", SecurityProtocol.SaslSsl)]
    [InlineData("SASL_SSL", SecurityProtocol.SaslSsl)]     // librdkafka canon
    [InlineData("sasl_plaintext", SecurityProtocol.SaslPlaintext)]
    [InlineData("Plaintext", SecurityProtocol.Plaintext)]
    public void SecurityProtocol_AcceptsBothCSharpAndKafkaForms(string value, SecurityProtocol expected)
    {
        var opts = new KafkaEndpointOptions { Brokers = "localhost:9092", SecurityProtocol = value };
        opts.BuildConsumerConfig().SecurityProtocol.Should().Be(expected);
    }

    [Theory]
    [InlineData("ScramSha256", SaslMechanism.ScramSha256)]
    [InlineData("SCRAM-SHA-256", SaslMechanism.ScramSha256)] // Kafka canon
    [InlineData("PLAIN", SaslMechanism.Plain)]
    public void SaslMechanism_AcceptsBothForms(string value, SaslMechanism expected)
    {
        var opts = new KafkaEndpointOptions
        {
            Brokers = "localhost:9092", SecurityProtocol = "SaslSsl",
            SaslMechanism = value, SaslUsername = "u", SaslPassword = "p",
        };
        opts.BuildConsumerConfig().SaslMechanism.Should().Be(expected);
    }

    [Theory]
    [InlineData("all", Acks.All)]
    [InlineData("-1", Acks.All)]
    [InlineData("1", Acks.Leader)]
    [InlineData("none", Acks.None)]
    public void Acks_KeepsItsHistoricalSynonyms(string value, Acks expected)
    {
        var opts = new KafkaEndpointOptions { Brokers = "localhost:9092", Acks = value };
        opts.BuildProducerConfig().Acks.Should().Be(expected);
    }

    [Theory]
    [InlineData("read_committed", IsolationLevel.ReadCommitted)]
    [InlineData("ReadCommitted", IsolationLevel.ReadCommitted)]
    [InlineData("readuncommitted", IsolationLevel.ReadUncommitted)]
    public void IsolationLevel_KeepsItsHistoricalSynonyms(string value, IsolationLevel expected)
    {
        var opts = new KafkaEndpointOptions { Brokers = "localhost:9092", IsolationLevel = value };
        opts.BuildConsumerConfig().IsolationLevel.Should().Be(expected);
    }

    // ── A mechanism without credentials is loud too ──

    [Fact]
    public void SaslMechanismWithoutCredentials_Fails()
    {
        var act = () => CreateEndpoint("securityProtocol=SaslSsl&saslMechanism=Plain");
        act.Should().Throw<ArgumentException>().WithMessage("*saslUsername*");
    }
}
