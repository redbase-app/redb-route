using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.As4;

/// <summary>
/// AS4 endpoint. Extends <see cref="EndpointBase{TOptions}"/>, which gives it statistics, health and Tsak
/// visibility. Produces an <see cref="As4Producer"/> (send) or an <see cref="As4Consumer"/> (receive).
/// </summary>
public sealed class As4Endpoint : EndpointBase<As4EndpointOptions>
{
    /// <summary>Logger of the owning component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Typed options, for the consumer and producer.</summary>
    internal As4EndpointOptions EndpointOptions => Options;

    /// <summary>The owning route context (registry lookups), via the component.</summary>
    internal IRouteContext? Context => (Component as ComponentBase)?.Context;

    /// <summary>
    /// Partner URL a producer posts to, rebuilt from a host-style URI (<c>as4s://ap.partner/as4</c> has the
    /// path <c>ap.partner/as4</c>), with <see cref="As4EndpointOptions.AddressQuery"/> as its query. Null for a
    /// receive URI (<c>as4:/as4/in</c>).
    /// </summary>
    internal string? PartnerUrl { get; }

    /// <summary>Creates an AS4 endpoint.</summary>
    public As4Endpoint(EndpointUri uri, As4Component component, As4EndpointOptions options)
        : base(uri, component, options)
    {
        ArgumentNullException.ThrowIfNull(component);
        Logger = component.Logger;

        if (uri.Path.Length > 0 && !uri.Path.StartsWith('/'))
            PartnerUrl = (options.UseTls ? "https://" : "http://") + uri.Path
                + (options.AddressQuery is null ? "" : "?" + options.AddressQuery);
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        RefuseOtherSidesOptions(EndpointRole.Producer);
        if (PartnerUrl is null)
        {
            // as4:?partner=...: the address is the partner's, scheme and query included.
            if (Uri.Path.Length > 0)
                throw new InvalidOperationException(
                    $"AS4 send endpoint '{EndpointUri.Sanitize(Uri.NormalizedKey)}' has a receive path; a send endpoint is " +
                    "as4s://host[:port]/path, or as4:?partner=... to use the partner's Address.");
            if (Options.UseTls || Options.AddressQuery is not null)
                throw new InvalidOperationException(
                    $"AS4 send endpoint '{EndpointUri.Sanitize(Uri.NormalizedKey)}' has no address, so as4s and addressQuery say nothing: " +
                    "the partner's Address gives scheme and query. Write as4:?partner=....");
        }
        return new As4Producer(this, Options);
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        RefuseOtherSidesOptions(EndpointRole.Consumer);
        return new As4Consumer(this, processor, Options);
    }

    /// <summary>
    /// An option only the other side reads (<see cref="EndpointRoleAttribute"/>, as the http: connector declares it)
    /// would bind and do nothing — <c>idempotentRepository</c> on a send looks like duplicate detection, <c>timeout</c>
    /// on a receive like a limit. Refused, naming the parameters and never their values.
    /// </summary>
    private void RefuseOtherSidesOptions(EndpointRole creating)
    {
        var written = redb.Route.Core.EndpointOptions.WrittenForOtherRole(Options.GetType(), Uri, creating);
        if (written.Count == 0)
            return;

        var side = creating == EndpointRole.Producer ? "send" : "receive";
        var other = creating == EndpointRole.Producer ? "receive" : "send";
        throw new InvalidOperationException(
            $"AS4 {side} endpoint '{EndpointUri.Sanitize(Uri.NormalizedKey)}' sets {string.Join(", ", written)}: " +
            $"only a {other} endpoint reads {(written.Count == 1 ? "it" : "them")}.");
    }
}
