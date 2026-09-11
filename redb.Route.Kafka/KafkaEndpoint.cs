using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Kafka;

/// <summary>
/// Kafka endpoint. The topic name comes from <see cref="EndpointUri.Path"/>.
/// Supports both producing and consuming with full transactional semantics.
/// </summary>
public sealed class KafkaEndpoint : EndpointBase<KafkaEndpointOptions>
{
    /// <summary>Kafka topic name (from URI path).</summary>
    public string TopicName { get; }

    /// <summary>Resolved connection factory from the registry (null if not configured).</summary>
    internal KafkaConnectionFactory? ResolvedFactory { get; }

    /// <summary>Creates a Kafka endpoint. The factory is resolved by the component
    /// BEFORE options validation (it may be the only source of the brokers).</summary>
    public KafkaEndpoint(EndpointUri uri, KafkaComponent component, KafkaEndpointOptions options,
        KafkaConnectionFactory? resolvedFactory = null)
        : base(uri, component, options)
    {
        TopicName = uri.Path;
        ResolvedFactory = resolvedFactory;
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new KafkaProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        if (string.IsNullOrWhiteSpace(Options.GroupId))
            throw new InvalidOperationException(
                $"The 'groupId' parameter is required for Kafka consumer on topic '{TopicName}'.");

        return new KafkaConsumer(this, processor, Options);
    }
}
