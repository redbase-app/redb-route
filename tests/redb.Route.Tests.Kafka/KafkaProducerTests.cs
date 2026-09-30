using System.Collections.Concurrent;
using System.Reflection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

public sealed class KafkaProducerTests
{
    private readonly KafkaComponent _component = new();

    private KafkaEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (KafkaEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var opts = new KafkaEndpointOptions { Brokers = "x:9092" };
        var act = () => new KafkaProducer(null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("kafka://t?brokers=x:9092");
        var act = () => new KafkaProducer(ep, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Process_BeforeStart_Throws()
    {
        var ep = CreateEndpoint("kafka://t?brokers=x:9092");
        var producer = new KafkaProducer(ep, new KafkaEndpointOptions { Brokers = "x:9092" });
        var exchange = new Exchange(new Message("test"));

        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not been started*");
    }

    [Fact]
    public void CreateProducer_ReturnsExpectedType()
    {
        var ep = CreateEndpoint("kafka://topic?brokers=localhost:9092");
        ep.CreateProducer().Should().BeOfType<KafkaProducer>();
    }

    // ── Commit of a Kafka transaction (review §6.1, docs/kafka/REVIEW-2026-09-28.md) ──

    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>A transactional producer whose librdkafka client is <paramref name="client"/>: no broker involved.</summary>
    private KafkaProducer TransactionalProducerOver(Confluent.Kafka.IProducer<string, byte[]> client)
    {
        var producer = (KafkaProducer)CreateEndpoint("kafka://t?brokers=x:9092&transactionalIdPrefix=tx").CreateProducer();
        typeof(KafkaProducer).GetField("_producer", Private)!.SetValue(producer, client);
        typeof(KafkaProducer).GetField("_transactionalId", Private)!.SetValue(producer, "tx-test");
        return producer;
    }

    private static bool MarkedForRebuild(KafkaProducer producer) =>
        (bool)typeof(KafkaProducer).GetField("_rebuild", Private)!.GetValue(producer)!;

    private static KafkaTransactionalSend Send() => new(
        new Confluent.Kafka.Message<string, byte[]> { Value = [1], Headers = new Confluent.Kafka.Headers() },
        new Exchange(new Message("x")));

    [Fact]
    public async Task A_commit_whose_outcome_is_unknown_is_not_aborted_and_the_producer_is_rebuilt()
    {
        // A commit that keeps timing out may have reached the coordinator and succeeded; the abort that followed was
        // no undo, only a call librdkafka refuses. The next incarnation's InitTransactions settles the outcome.
        var client = Substitute.For<Confluent.Kafka.IProducer<string, byte[]>>();
        client.When(c => c.CommitTransaction()).Do(_ =>
            throw new Confluent.Kafka.KafkaRetriableException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_TimedOut)));
        var producer = TransactionalProducerOver(client);

        var commit = () => producer.CommitTransactionAsync([Send()], offsets: null, CancellationToken.None);

        await commit.Should().ThrowAsync<Confluent.Kafka.KafkaRetriableException>();
        client.Received(3).CommitTransaction();
        client.DidNotReceive().AbortTransaction();
        client.DidNotReceive().AbortTransaction(Arg.Any<TimeSpan>());
        MarkedForRebuild(producer).Should().BeTrue();
    }

    [Fact]
    public async Task A_commit_that_requires_an_abort_is_aborted_and_neither_call_takes_our_timeout()
    {
        // librdkafka strongly recommends CommitTransaction/AbortTransaction without a timeout: its API timeouts do not
        // match the protocol requests.
        var client = Substitute.For<Confluent.Kafka.IProducer<string, byte[]>>();
        client.When(c => c.CommitTransaction()).Do(_ =>
            throw new Confluent.Kafka.KafkaTxnRequiresAbortException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.InvalidTxnState)));
        var producer = TransactionalProducerOver(client);

        var commit = () => producer.CommitTransactionAsync([Send()], offsets: null, CancellationToken.None);

        await commit.Should().ThrowAsync<Confluent.Kafka.KafkaTxnRequiresAbortException>();
        client.DidNotReceive().CommitTransaction(Arg.Any<TimeSpan>());
        client.Received(1).AbortTransaction();
        client.DidNotReceive().AbortTransaction(Arg.Any<TimeSpan>());
        MarkedForRebuild(producer).Should().BeFalse("an aborted transaction leaves the producer usable");
    }
}
