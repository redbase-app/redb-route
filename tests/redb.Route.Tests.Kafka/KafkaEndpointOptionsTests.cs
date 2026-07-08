using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

public sealed class KafkaEndpointOptionsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var opts = new KafkaEndpointOptions();

        opts.Brokers.Should().BeEmpty();
        opts.SecurityProtocol.Should().Be("Plaintext");
        opts.AutoOffsetReset.Should().Be("Latest");
        opts.MaxPollRecords.Should().Be(0);
        opts.PollTimeoutMs.Should().Be(1000);
        opts.BreakOnFirstError.Should().BeFalse();
        opts.TopicIsPattern.Should().BeFalse();
        opts.Acks.Should().Be("Leader");
        opts.Retries.Should().Be(3);
        opts.RecordMetadata.Should().BeFalse();
        opts.Transacted.Should().BeFalse();
        opts.TransactionIdPrefix.Should().Be("redb-kafka");
    }

    [Fact]
    public void Validate_NoBrokers_Throws()
    {
        var opts = new KafkaEndpointOptions { Brokers = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*brokers*");
    }

    [Fact]
    public void Validate_NegativeMaxPollRecords_Throws()
    {
        var opts = new KafkaEndpointOptions { Brokers = "localhost:9092", MaxPollRecords = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_NegativePollTimeoutMs_Throws()
    {
        var opts = new KafkaEndpointOptions { Brokers = "localhost:9092", PollTimeoutMs = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_NegativeRetries_Throws()
    {
        var opts = new KafkaEndpointOptions { Brokers = "localhost:9092", Retries = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var opts = new KafkaEndpointOptions
        {
            Brokers = "localhost:9092",
            GroupId = "test-group",
            MaxPollRecords = 100,
            PollTimeoutMs = 500,
            Retries = 5
        };

        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void BuildConsumerConfig_SetsBootstrapServers()
    {
        var opts = new KafkaEndpointOptions { Brokers = "b1:9092,b2:9092", GroupId = "grp1" };
        var config = opts.BuildConsumerConfig();

        config.BootstrapServers.Should().Be("b1:9092,b2:9092");
        config.GroupId.Should().Be("grp1");
        config.EnableAutoCommit.Should().BeFalse();
    }

    [Fact]
    public void BuildConsumerConfig_ParsesAutoOffsetReset()
    {
        var opts = new KafkaEndpointOptions { Brokers = "x:9092", GroupId = "g", AutoOffsetReset = "Earliest" };
        var config = opts.BuildConsumerConfig();
        config.AutoOffsetReset.Should().Be(Confluent.Kafka.AutoOffsetReset.Earliest);
    }

    [Fact]
    public void BuildProducerConfig_SetsAcksAndRetries()
    {
        var opts = new KafkaEndpointOptions { Brokers = "x:9092", Acks = "All", Retries = 10 };
        var config = opts.BuildProducerConfig();

        config.Acks.Should().Be(Confluent.Kafka.Acks.All);
        config.MessageSendMaxRetries.Should().Be(10);
    }

    [Fact]
    public void BuildProducerConfig_Transacted_EnablesIdempotence()
    {
        var opts = new KafkaEndpointOptions { Brokers = "x:9092", Transacted = true, TransactionIdPrefix = "test" };
        var config = opts.BuildProducerConfig();

        // `transacted=true` is deferred-idempotent, NOT Kafka EOS: BuildProducerConfig deliberately
        // enables idempotence + Acks.All but does NOT set transactional.id — configuring one would put
        // librdkafka into transactional mode requiring BeginTransaction/Commit around every Produce,
        // which the connector does not implement (a deferred Produce would throw "Local: Erroneous
        // state"). See redb.Route.Kafka/KafkaEndpointOptions.BuildProducerConfig + KAFKA_TRANSACTIONS_TODO.md.
        config.EnableIdempotence.Should().BeTrue();
        config.Acks.Should().Be(Confluent.Kafka.Acks.All);
        config.TransactionalId.Should().BeNull("TransactionIdPrefix must not leak into transactional.id");
    }

    [Theory]
    [InlineData("earliest", Confluent.Kafka.AutoOffsetReset.Earliest)]
    [InlineData("latest", Confluent.Kafka.AutoOffsetReset.Latest)]
    [InlineData("error", Confluent.Kafka.AutoOffsetReset.Error)]
    [InlineData("unknown", Confluent.Kafka.AutoOffsetReset.Latest)]
    public void BuildConsumerConfig_AutoOffsetReset_AllValues(string input, Confluent.Kafka.AutoOffsetReset expected)
    {
        var opts = new KafkaEndpointOptions { Brokers = "x:9092", GroupId = "g", AutoOffsetReset = input };
        var config = opts.BuildConsumerConfig();
        config.AutoOffsetReset.Should().Be(expected);
    }

    [Theory]
    [InlineData("none", Confluent.Kafka.Acks.None)]
    [InlineData("0", Confluent.Kafka.Acks.None)]
    [InlineData("leader", Confluent.Kafka.Acks.Leader)]
    [InlineData("1", Confluent.Kafka.Acks.Leader)]
    [InlineData("all", Confluent.Kafka.Acks.All)]
    [InlineData("-1", Confluent.Kafka.Acks.All)]
    [InlineData("unknown", Confluent.Kafka.Acks.Leader)]
    public void BuildProducerConfig_Acks_AllValues(string input, Confluent.Kafka.Acks expected)
    {
        var opts = new KafkaEndpointOptions { Brokers = "x:9092", Acks = input };
        var config = opts.BuildProducerConfig();
        config.Acks.Should().Be(expected);
    }

    [Fact]
    public void BuildConsumerConfig_WithSaslSecurity_AppliesCredentials()
    {
        var opts = new KafkaEndpointOptions
        {
            Brokers = "kafka:9093",
            SecurityProtocol = "SaslPlaintext",
            SaslMechanism = "Plain",
            SaslUsername = "user",
            SaslPassword = "pass",
            GroupId = "g"
        };

        var config = opts.BuildConsumerConfig();

        config.SecurityProtocol.Should().Be(Confluent.Kafka.SecurityProtocol.SaslPlaintext);
        config.SaslMechanism.Should().Be(Confluent.Kafka.SaslMechanism.Plain);
        config.SaslUsername.Should().Be("user");
        config.SaslPassword.Should().Be("pass");
    }
}
