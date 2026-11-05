using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Waits, once per test run, until the test cluster can serve what a test is about to use. A freshly started
/// cluster answers metadata requests long before it has loaded its internal topics: a consumer group then joins
/// late (a test's observation window passes before the first record arrives) and <c>InitTransactions</c> fails
/// with "Coordinator load in progress" after its own timeout. Tests await these checks before they start
/// measuring, so their windows time the code under test, not the broker's warm-up.
/// </summary>
internal static class KafkaReadiness
{
    private const string BootstrapServers = "localhost:29092,localhost:29094,localhost:29096";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    private static readonly Lazy<Task> Groups = new(() => Task.Run(WaitForGroupCoordinator));
    private static readonly Lazy<Task> Transactions = new(() => Task.Run(WaitForTransactionCoordinator));

    /// <summary>A consumer group can join and get partitions assigned.</summary>
    public static Task GroupCoordinatorAsync() => Groups.Value;

    /// <summary>A transactional producer can initialize its transactions.</summary>
    public static Task TransactionCoordinatorAsync() => Transactions.Value;

    /// <summary>
    /// Creates a topic with one partition, so the records a test produces are consumed in the order they were
    /// sent, and waits until its leader is known. Replicated like the cluster's auto-created topics.
    /// </summary>
    public static async Task<string> CreateSinglePartitionTopicAsync(string prefix)
    {
        var topic = $"{prefix}-{Guid.NewGuid():N}";
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 3 }]);
        await WaitForLeaders(admin, topic, partitions: 1);
        return topic;
    }

    /// <summary>Waits until the metadata of <paramref name="topic"/> names a leader for each of its partitions.</summary>
    public static async Task WaitForLeaders(IAdminClient admin, string topic, int partitions)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            var metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(10)).Topics.SingleOrDefault(t => t.Topic == topic);
            if (metadata is { Error.IsError: false } && metadata.Partitions.Count == partitions
                && metadata.Partitions.All(p => p.Leader >= 0))
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"topic {topic} has no leader for each of its {partitions} partitions after {Limit}.");
            await Task.Delay(200);
        }
    }

    private static async Task WaitForGroupCoordinator()
    {
        var topic = await CreateSinglePartitionTopicAsync("ready-group");
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"ready-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);
        var deadline = DateTime.UtcNow + Limit;
        try
        {
            while (consumer.Assignment.Count == 0)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"no consumer group got a partition assigned within {Limit}.");
                consumer.Consume(TimeSpan.FromMilliseconds(250));
            }
        }
        finally
        {
            consumer.Close();
        }
    }

    private static void WaitForTransactionCoordinator()
    {
        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            using var producer = new ProducerBuilder<Null, Null>(new ProducerConfig
            {
                BootstrapServers = BootstrapServers,
                TransactionalId = $"ready-{Guid.NewGuid():N}",
            }).Build();
            try
            {
                producer.InitTransactions(TimeSpan.FromSeconds(30));
                return;
            }
            catch (KafkaRetriableException) when (DateTime.UtcNow < deadline)
            {
                // The transaction coordinator is still loading its log; ask again.
            }
        }
    }
}
