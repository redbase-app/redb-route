using System.Security.Claims;

namespace redb.Route.Abstractions;

/// <summary>
/// The identity of the caller an exchange came from (Apache Camel <c>Exchange.AUTHENTICATION</c>).
/// A consumer that knows who sent the request stores the principal here, and processors, policies and
/// tools read it from here rather than from transport-specific headers.
/// <para>
/// It is an exchange <b>property</b>, not a header, on purpose. Headers are filled from the wire, so a
/// caller can send any header name it likes, and producers bridge headers on to the next system.
/// Properties are written only by code running in this process and never travel to a broker. Every
/// exchange copy (child, linked child, clone, snapshot) inherits properties, so a sub-route, a split part
/// or a tool call sees the identity of the request that started it.
/// </para>
/// <para>
/// The server-side HTTP-family consumers fill it from the shared host's principal resolver or from the
/// transport's own authenticate hook. A route that validates credentials itself — a bearer token checked
/// in a processor — records the outcome the same way, with <see cref="Set"/>.
/// </para>
/// </summary>
public static class ExchangePrincipal
{
    /// <summary>Exchange property key holding the principal (Camel: <c>Exchange.AUTHENTICATION</c>).</summary>
    public const string PropertyKey = "CamelAuthentication";

    /// <summary>Returns the caller's principal, or <c>null</c> when the exchange carries no identity.</summary>
    public static ClaimsPrincipal? Get(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return exchange.Properties.TryGetValue(PropertyKey, out var raw) ? raw as ClaimsPrincipal : null;
    }

    /// <summary>Stores <paramref name="principal"/> as the caller's identity; <c>null</c> removes it.</summary>
    public static void Set(IExchange exchange, ClaimsPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (principal is null)
            exchange.Properties.Remove(PropertyKey);
        else
            exchange.Properties[PropertyKey] = principal;
    }
}
