using redb.Route.Abstractions;
using redb.Route.Core;

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

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new KafkaEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new KafkaEndpoint(uri, this, options);
    }
}
