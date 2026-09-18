using System.Security.Claims;
using redb.Route.Abstractions;

namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Default <see cref="IToolClaimsSource"/>: the claims of the caller an exchange came from.
/// <para>
/// The principal is read from <see cref="ExchangePrincipal"/>, i.e. a <b>property</b> of the exchange
/// that only in-process code can write — a caller cannot send it as a header, and it does not travel
/// to a broker. That makes it the only identity a tool requirement may be checked against: a header
/// naming a user (such as <c>llm.user.id</c>) is audit information, not proof.
/// </para>
/// <para>
/// Values are collected from the scope claim types issuers actually use — <c>scope</c> (OAuth 2.0 /
/// OIDC), <c>scp</c> and <c>http://schemas.microsoft.com/identity/claims/scope</c> (Microsoft Entra
/// ID) — split on <b>whitespace only</b> and de-duplicated with ordinal comparison, because one logical
/// set can arrive as several claims with padded values. The delimiter is space by specification
/// (RFC 6749 §3.3) and a comma is a legal character <i>inside</i> a scope token: splitting on it would
/// turn one granted scope into two and widen the caller's rights, which is the one direction a security
/// check must never fail in. A deployment whose issuer really emits comma-separated lists registers its
/// own <see cref="IToolClaimsSource"/>. Claim values are compared as-is; scope names are case-sensitive.
/// </para>
/// <para>
/// A principal whose identity is not authenticated counts as <b>no principal</b> (<c>null</c>), not as
/// an empty set: an anonymous caller cannot satisfy a requirement. This puts a requirement on hosts: a
/// resolver must return an identity carrying an authentication type — <c>new ClaimsIdentity(claims)</c>
/// without one reports <c>IsAuthenticated == false</c> and would leave claim-required tools denied
/// (fail closed, but for a reason the log must make visible). A run that has no inbound request — a
/// scheduler-fired one, or one born after a broker hop, where properties do not survive — likewise has
/// no principal. A consumer that establishes identity by itself (an AS2 signature check, a bearer token
/// validated in a processor) must store the outcome the same way, with <see cref="ExchangePrincipal.Set"/>
/// — a <b>property</b>, never a report header the caller could have written.
/// </para>
/// <para>
/// Deployments with a different vocabulary register their own <see cref="IToolClaimsSource"/>; it
/// replaces this one.
/// </para>
/// </summary>
public sealed class ExchangePrincipalClaimsSource : IToolClaimsSource
{
    private static readonly string[] ScopeClaimTypes =
    [
        "scope",                                              // OAuth 2.0 / OIDC
        "scp",                                                // Microsoft Entra ID (v2)
        "http://schemas.microsoft.com/identity/claims/scope"  // Microsoft Entra ID (v1)
    ];

    private static readonly char[] Separators = [' ', '\t', '\r', '\n'];

    /// <inheritdoc />
    public IReadOnlyCollection<string>? GetClaims(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        var principal = ExchangePrincipal.Get(exchange);
        if (principal?.Identity is not { IsAuthenticated: true }) return null;

        var claims = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claimType in ScopeClaimTypes)
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                foreach (var value in claim.Value.Split(
                             Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    claims.Add(value);
                }
            }
        }

        return claims;
    }
}
