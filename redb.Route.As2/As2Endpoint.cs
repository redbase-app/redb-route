using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.As2;

/// <summary>
/// AS2 endpoint. Extends <see cref="EndpointBase{TOptions}"/> so it gets statistics, health and Tsak
/// visibility for free (see <c>docs/as2/00-ARCHITECTURE.md §3</c>). Produces an <see cref="As2Producer"/>
/// (send) or an <see cref="As2Consumer"/> (receive server) depending on the DSL side.
/// </summary>
public sealed class As2Endpoint : EndpointBase<As2EndpointOptions>
{
    /// <summary>Logger inherited from the owning component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Typed options, exposed to the consumer/producer.</summary>
    internal As2EndpointOptions EndpointOptions => Options;

    /// <summary>The owning route context (for registry lookups), via the component.</summary>
    internal IRouteContext? Context => (Component as ComponentBase)?.Context;

    /// <summary>Creates an AS2 endpoint.</summary>
    public As2Endpoint(EndpointUri uri, As2Component component, As2EndpointOptions options)
        : base(uri, component, options)
    {
        ArgumentNullException.ThrowIfNull(component);
        Logger = component.Logger;

        // Producer URIs are host-style (as2s://partner/as2 ⇒ path "partner/as2"); reconstruct the partner
        // URL from the scheme + path. Consumer URIs are path-style ("/inbound/orders") and skip this.
        if (string.IsNullOrEmpty(options.PartnerUrl) && uri.Path.Length > 0 && !uri.Path.StartsWith('/'))
            options.PartnerUrl = (options.UseTls ? "https://" : "http://") + uri.Path;

        _headerFilter = new EndpointHeaderFilter(this, options.HeaderFilterStrategy);
    }

    private readonly EndpointHeaderFilter _headerFilter;

    /// <summary>
    /// The header filter strategy of this endpoint: the one <c>headerFilterStrategy=#name</c> names, or
    /// <see cref="As2HeaderFilterStrategy.Default"/>. A name the registry does not hold fails when the producer or
    /// consumer is built.
    /// </summary>
    internal IHeaderFilterStrategy HeaderFilterStrategy => _headerFilter.Or(As2HeaderFilterStrategy.Default);

    /// <inheritdoc />
    public override IProducer CreateProducer() => new As2Producer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor) => Options.Mode == As2ReceiveMode.Mdn
        ? new As2MdnReceiver(this, processor, Options)
        : new As2Consumer(this, processor, Options);
}
