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

    /// <summary>Creates a Kafka endpoint.</summary>
    public KafkaEndpoint(EndpointUri uri, KafkaComponent component, KafkaEndpointOptions options)
        : base(uri, component, options)
    {
        TopicName = uri.Path;

        // Resolve named ConnectionFactory from registry if specified
        if (!string.IsNullOrEmpty(options.ConnectionFactory) && component.Context is not null)
        {
            ResolvedFactory = component.Context.GetFromRegistry<KafkaConnectionFactory>(options.ConnectionFactory);
        }

        // If brokers not set in URI but factory provides them, apply
        if (string.IsNullOrWhiteSpace(options.Brokers) && ResolvedFactory is not null)
            options.Brokers = ResolvedFactory.Brokers;
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
