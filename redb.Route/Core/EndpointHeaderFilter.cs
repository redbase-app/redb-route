using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// The header filter strategy of one endpoint: the one its <c>headerFilterStrategy=#name</c> option names, resolved from
/// the context registry once (by whichever producer or consumer of the endpoint is built first, on whatever thread),
/// or the transport's default. A transport keeps one per endpoint.
/// </summary>
public sealed class EndpointHeaderFilter
{
    private readonly Lazy<IHeaderFilterStrategy?> _configured;

    /// <summary>Creates the filter of <paramref name="endpoint"/> from its option value.</summary>
    /// <param name="endpoint">The endpoint whose component's context holds the registry.</param>
    /// <param name="reference">The value of the endpoint's <c>headerFilterStrategy</c> option; <c>null</c> when unset.</param>
    public EndpointHeaderFilter(IEndpoint endpoint, string? reference)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        // A failed resolution is not cached: a bean registered afterwards is found on the next attempt.
        _configured = new Lazy<IHeaderFilterStrategy?>(() => HeaderFilterStrategies.Resolve(
                (endpoint.Component as ComponentBase)?.Context, reference,
                $"{endpoint.Uri.Scheme} endpoint '{EndpointUri.Sanitize(endpoint.Uri.NormalizedKey)}'"),
            LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>
    /// The strategy the endpoint names, or <paramref name="transportDefault"/> when it names none. A name the registry
    /// does not hold fails here.
    /// </summary>
    public IHeaderFilterStrategy Or(IHeaderFilterStrategy transportDefault) => _configured.Value ?? transportDefault;

    /// <summary>
    /// The strategy the endpoint names, or <c>null</c> when it names none: for a component with no default strategy
    /// (<c>direct-vm:</c>, <c>vm:</c>), which then carries every header as it always has. A name the registry does not
    /// hold fails here.
    /// </summary>
    public IHeaderFilterStrategy? Configured => _configured.Value;
}
