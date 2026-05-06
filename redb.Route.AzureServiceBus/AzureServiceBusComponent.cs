using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// Azure Service Bus component. Scheme: <c>asb</c>.
/// <para>
/// URI format: <c>asb://queue-name?connectionString=...</c>
///             <c>asb://topic-name?connectionString=...&amp;subscriptionName=my-sub</c>
/// </para>
/// </summary>
public sealed class AzureServiceBusComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "asb";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new AzureServiceBusEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new AzureServiceBusEndpoint(uri, this, options);
    }
}
