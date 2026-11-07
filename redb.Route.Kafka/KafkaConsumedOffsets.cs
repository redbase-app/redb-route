using Confluent.Kafka;
using Confluent.Kafka.Admin;
using redb.Route.Abstractions;

namespace redb.Route.Kafka;

/// <summary>
/// The offsets a Kafka consumer is about to commit for the exchange it handed the route, offered to a Kafka-transactional
/// producer of the same cluster in that route. Committed inside the producer's transaction
/// (<c>SendOffsetsToTransaction</c>), they become visible together with its sends — exactly-once within Kafka — and the
/// consumer does not commit them itself.
/// <para>
/// Claimable only while the consumer is still waiting for the route: a copy the route handed on asynchronously (seda,
/// <c>.Threads()</c>) commits after the consumer moved on, with a group generation that may no longer be current, and
/// must not take them. One transaction takes them at most.
/// </para>
/// </summary>
internal sealed class KafkaConsumedOffsets
{
    /// <summary>Exchange property the consumer offers the offsets under.</summary>
    internal const string PropertyKey = "KafkaConsumedOffsets";

    private const int Open = 0, Claimed = 1, Closed = 2;

    private readonly IConsumer<string, byte[]> _consumer;
    private readonly KafkaCommitAction _commit;
    private int _state;

    public KafkaConsumedOffsets(IConsumer<string, byte[]> consumer, KafkaCommitAction commit, string? cluster)
    {
        _consumer = consumer;
        _commit = commit;
        Cluster = cluster;
    }

    /// <summary>The consumer's cluster id (<see cref="ClusterIdOf"/>); null when the consumer could not read it.</summary>
    public string? Cluster { get; }

    /// <summary>The positions to commit: the next offset to read, per partition.</summary>
    public IReadOnlyList<TopicPartitionOffset> Offsets => _commit.NextOffsets;

    /// <summary>The consumer's group metadata, read when the transaction commits: it carries the current generation.</summary>
    public IConsumerGroupMetadata GroupMetadata => _consumer.ConsumerGroupMetadata;

    /// <summary>Takes the offsets for a transaction; false when another took them or the consumer has finished.</summary>
    public bool TryClaim() => Interlocked.CompareExchange(ref _state, Claimed, Open) == Open;

    /// <summary>The transaction that took them did not commit: they are the consumer's again.</summary>
    public void Release() => Interlocked.CompareExchange(ref _state, Open, Claimed);

    /// <summary>The transaction committed them: the consumer must not commit them again.</summary>
    public void MarkCommitted() => _commit.MarkCommitted();

    /// <summary>The route returned to the consumer: no transaction may take them any more.</summary>
    public void Close() => Interlocked.CompareExchange(ref _state, Closed, Open);

    /// <summary>True once the route returned to the consumer: a transaction that did not take them before cannot now.</summary>
    public bool IsClosed => Volatile.Read(ref _state) == Closed;

    /// <summary>The offsets a Kafka consumer offered on <paramref name="exchange"/>, of whatever cluster, if any.</summary>
    public static KafkaConsumedOffsets? OfferedOn(IExchange exchange) =>
        exchange.Properties.TryGetValue(PropertyKey, out var value) && value is KafkaConsumedOffsets offsets
            ? offsets
            : null;

    /// <summary>
    /// A cluster's identity for matching a consumer with a producer: the cluster id its brokers report, read through the
    /// client's own connection (<paramref name="handle"/>). Brokers listed differently on the two ends are still one
    /// cluster; a producer of another cluster cannot commit these offsets, they belong to the consumer group of this one.
    /// </summary>
    public static async Task<string> ClusterIdOf(Handle handle, TimeSpan timeout)
    {
        using var admin = new DependentAdminClientBuilder(handle).Build();
        var cluster = await admin.DescribeClusterAsync(new DescribeClusterOptions { RequestTimeout = timeout })
            .ConfigureAwait(false);
        return cluster.ClusterId ?? throw new InvalidOperationException("The Kafka cluster reported no cluster id.");
    }
}
