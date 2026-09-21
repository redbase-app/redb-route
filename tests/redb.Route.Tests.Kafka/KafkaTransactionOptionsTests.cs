using redb.Route.Kafka;
using KafkaDsl = redb.Route.Kafka.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// <c>transactionalIdPrefix</c> switches a producer to Kafka transactions: it requires what a transactional producer is
/// (acks=all, idempotent) and refuses the back door of a raw <c>transactional.id</c>, which would put librdkafka into
/// transactional mode with nobody opening transactions.
/// </summary>
public sealed class KafkaTransactionOptionsTests
{
    private static KafkaEndpointOptions Options(Action<KafkaEndpointOptions>? configure = null)
    {
        var options = new KafkaEndpointOptions { Brokers = "localhost:9092", TransactionalIdPrefix = "orders" };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void A_transactional_producer_is_idempotent_with_acks_all()
    {
        var config = Options(o => o.EnableIdempotence = null).BuildProducerConfig();

        config.EnableIdempotence.Should().BeTrue();
        config.Acks.Should().Be(Confluent.Kafka.Acks.All);
        config.TransactionalId.Should().BeNull("the producer appends its own identity to the prefix");
    }

    [Fact]
    public void It_requires_acks_all()
    {
        var act = () => Options(o => o.Acks = "Leader").Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*transactionalIdPrefix*acks=all*");
    }

    [Fact]
    public void It_contradicts_turning_idempotence_off()
    {
        var act = () => Options(o => o.EnableIdempotence = false).Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*transactionalIdPrefix*enableIdempotence=false*");
    }

    [Fact]
    public void An_empty_prefix_is_refused()
    {
        var act = () => Options(o => o.TransactionalIdPrefix = " ").Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*transactionalIdPrefix*empty*");
    }

    [Fact]
    public void A_raw_transactional_id_in_additional_properties_is_refused()
    {
        var act = () => new KafkaEndpointOptions
        {
            Brokers = "localhost:9092",
            AdditionalProperties = { ["transactional.id"] = "orders" },
        }.Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*transactional.id*transactionalIdPrefix*");
    }

    [Fact]
    public void A_misspelt_option_is_refused_by_name_without_its_value()
    {
        var options = new KafkaEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["brokers"] = "localhost:9092",
            ["transactionalIdPrefx"] = "orders",
            ["saslPasword"] = "s3cr3t",
        });

        var act = () => options.Validate();

        act.Should().Throw<ArgumentException>()
            .WithMessage("*'transactionalIdPrefx' is not an option*'saslPasword' is not an option*")
            .Which.Message.Should().NotContain("s3cr3t", "a misspelt secret must not reach the log");
    }

    [Fact]
    public void An_option_whose_value_does_not_convert_is_refused()
    {
        var options = new KafkaEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["brokers"] = "localhost:9092", ["retries"] = "three" });

        var act = () => options.Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*'retries'*not a Int32*");
    }

    [Fact]
    public void The_builder_writes_the_prefix_out_and_leaves_it_out_unset()
    {
        KafkaDsl.Topic("t").TransactionalIdPrefix("orders").Build().Should().Contain("transactionalIdPrefix=orders");
        KafkaDsl.Topic("t").Build().Should().NotContain("transactionalIdPrefix");
    }

    [Theory]
    [InlineData("b:2, a:1", "a:1,b:2")]
    [InlineData("A:1,a:1,b:2", "a:1,b:2")]
    [InlineData(" a:1 ", "a:1")]
    public void A_cluster_is_its_brokers_in_any_order(string servers, string cluster) =>
        KafkaConsumedOffsets.ClusterOf(servers).Should().Be(cluster);
}
