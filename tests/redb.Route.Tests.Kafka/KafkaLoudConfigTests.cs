using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
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

    // ── additionalProperties: both sources, one list of refused properties (review R1, docs/kafka/REVIEW-2026-09-28.md) ──

    private static KafkaEndpointOptions WithProperty(string name, string value, string? transactionalIdPrefix = null) =>
        new()
        {
            Brokers = "localhost:9092",
            TransactionalIdPrefix = transactionalIdPrefix,
            AdditionalProperties = { [name] = value },
        };

    /// <summary>Creates an endpoint through a context whose registry holds connection factory 'cf'.</summary>
    private static IEndpoint EndpointWithFactory(KafkaConnectionFactory factory, string parameters)
    {
        var context = new RouteContext();
        context.AddComponent(new KafkaComponent());
        context.AddToRegistry("cf", factory);
        return context.GetEndpoint($"kafka://orders?connectionFactory=cf&{parameters}");
    }

    [Fact]
    public void AutoCommit_InAdditionalProperties_IsRefused()
    {
        // Applied last, it overrode the connector's enable.auto.commit=false: records read but not processed were
        // committed on a timer, whatever ackMode said.
        var act = () => WithProperty("enable.auto.commit", "true").Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*enable.auto.commit=true*additionalProperties*ackMode*");
    }

    [Fact]
    public void AutoCommit_InTheFactorysAdditionalProperties_IsRefused()
    {
        var factory = new KafkaConnectionFactory { Brokers = "localhost:9092", AdditionalProperties = { ["enable.auto.commit"] = "true" } };

        var act = () => EndpointWithFactory(factory, "groupId=g");

        act.Should().Throw<ArgumentException>().WithMessage("*enable.auto.commit=true*connection factory 'cf'*");
    }

    [Fact]
    public void TransactionalId_InTheFactorysAdditionalProperties_IsRefused()
    {
        // The endpoint's own additionalProperties refused it; the factory's were a way round.
        var factory = new KafkaConnectionFactory { Brokers = "localhost:9092", AdditionalProperties = { ["transactional.id"] = "orders" } };

        var act = () => EndpointWithFactory(factory, "acks=all");

        act.Should().Throw<ArgumentException>().WithMessage("*transactional.id*connection factory 'cf'*transactionalIdPrefix*");
    }

    [Theory]
    [InlineData("enable.idempotence", "false")]
    [InlineData("acks", "1")]
    [InlineData("acks", "leader")]
    public void A_property_contradicting_a_transactional_producer_is_refused(string name, string value)
    {
        var act = () => WithProperty(name, value, transactionalIdPrefix: "orders").Validate();

        act.Should().Throw<ArgumentException>().WithMessage($"*'{name}={value}'*transactionalIdPrefix*");
    }

    // ── TLS options ──

    [Theory]
    [InlineData("none", Confluent.Kafka.SslEndpointIdentificationAlgorithm.None)]
    [InlineData("", Confluent.Kafka.SslEndpointIdentificationAlgorithm.None)]
    [InlineData("HTTPS", Confluent.Kafka.SslEndpointIdentificationAlgorithm.Https)]
    public void HostnameVerification_IsParsedByValue(string value, Confluent.Kafka.SslEndpointIdentificationAlgorithm expected)
    {
        // "none" used to mean https: only the empty string turned the check off.
        var options = new KafkaEndpointOptions { Brokers = "localhost:9092", SslEndpointIdentificationAlgorithm = value };

        options.BuildProducerConfig().SslEndpointIdentificationAlgorithm.Should().Be(expected);
    }

    [Theory]
    [InlineData("kafka://orders?brokers=localhost:9092&sslEndpointIdentificationAlgorithm=nope")]
    [InlineData("kafka://orders?brokers=localhost:9092&sslEndpointIdentificationAlgorithm=off")]
    public void HostnameVerification_Typo_FailsEndpointCreation(string uri)
    {
        var act = () => new KafkaComponent().CreateEndpoint(EndpointUriParser.Parse(uri));

        act.Should().Throw<ArgumentException>().WithMessage("*sslEndpointIdentificationAlgorithm*https*none*");
    }

    [Fact]
    public void HostnameVerification_Typo_InTheFactory_FailsTheBuild()
    {
        var factory = new KafkaConnectionFactory { Brokers = "localhost:9092", SslEndpointIdentificationAlgorithm = "nope" };

        var act = () => factory.BuildProducerConfig();

        act.Should().Throw<ArgumentException>().WithMessage("*sslEndpointIdentificationAlgorithm*");
    }

    [Theory]
    [InlineData("securityProtocol=SaslPlaintext&saslMechanism=Plain&saslUsername=u&saslPassword=p", true)]
    [InlineData("securityProtocol=SaslSsl&saslMechanism=Plain&saslUsername=u&saslPassword=p", false)]
    [InlineData("securityProtocol=SaslPlaintext&saslMechanism=ScramSha512&saslUsername=u&saslPassword=p", false)]
    public async Task SaslPlain_OverPlaintext_WarnsThatThePasswordGoesUnencrypted(string parameters, bool warned)
    {
        var capture = new CapturingLoggerProvider();
        await using var context = new RouteContext(loggerFactory: Microsoft.Extensions.Logging.LoggerFactory.Create(
            b => b.AddProvider(capture)));
        var component = new KafkaComponent();
        context.AddComponent(component);
        var endpoint = (KafkaEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"kafka://orders?brokers=localhost:1&{parameters}"));

        // A plain producer builds its client on start without reaching a broker.
        var producer = (KafkaProducer)endpoint.CreateProducer();
        await producer.Start();
        await producer.Stop();

        capture.Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                                 && e.Message.Contains("sends the password unencrypted"))
            .Should().Be(warned);
    }

    [Theory]
    [InlineData("enable.auto.commit", "false", null)]
    [InlineData("enable.idempotence", "true", "orders")]
    [InlineData("acks", "all", "orders")]
    [InlineData("acks", "-1", "orders")]
    [InlineData("acks", "1", null)]
    public void A_property_that_agrees_with_the_connector_is_left_alone(string name, string value, string? prefix)
    {
        var act = () => WithProperty(name, value, prefix).Validate();

        act.Should().NotThrow();
    }
}
