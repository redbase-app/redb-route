using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Kafka;

/// <summary>
/// Apache Kafka transport component for redb.Route.
/// Scheme: <c>kafka</c>.
/// <para>
/// URI format: <c>kafka://topic-name?brokers=host:9092&amp;groupId=group&amp;autoOffsetReset=Earliest</c>
/// </para>
/// </summary>
public sealed partial class KafkaComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "kafka";

    /// <summary>Structured XML form (Route-XML Ф0 §7.2): <c>&lt;kafka topic="orders"/&gt;</c>.</summary>
    public override string? StructuredPathSynonym => "topic";


    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new KafkaEndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // Resolve the named ConnectionFactory BEFORE Validate(): the factory may be the only
        // source of the brokers. A set-but-unknown name fails loud — a typo must never
        // silently fall back to URI parameters (Ф11 Ж-1).
        KafkaConnectionFactory? factory = null;
        if (!string.IsNullOrEmpty(options.ConnectionFactory))
        {
            factory = Context.GetRequiredFromRegistry<KafkaConnectionFactory>(options.ConnectionFactory);
            if (string.IsNullOrWhiteSpace(options.Brokers))
                options.Brokers = factory.Brokers;
            // Ревью дуги (M5/M6): Validate() and the consumer's groupId check see only the
            // options, so factory values that act as defaults must land there BEFORE validation -
            // otherwise "credentials via a named connectionFactory" (the error text's own advice)
            // and the factory's documented default GroupId can never work. URI still wins:
            // the copy happens only when the URI supplied nothing.
            if (string.IsNullOrWhiteSpace(options.SaslUsername))
                options.SaslUsername = factory.SaslUsername;
            if (string.IsNullOrWhiteSpace(options.SaslPassword))
                options.SaslPassword = factory.SaslPassword;
            if (string.IsNullOrWhiteSpace(options.GroupId))
                options.GroupId = factory.GroupId;
        }

        options.Validate();

        return new KafkaEndpoint(uri, this, options, factory);
    }
}
