using redb.Route.Abstractions;

namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Resolves the claims held by the principal of an agent run. The engine consults it before
/// dispatching a tool whose <see cref="LlmToolSafety.RequiredClaims"/> is not empty.
/// <para>
/// Returning <c>null</c> means "this run has no verifiable principal"; returning an empty set means
/// "a principal exists and holds nothing". Both deny a claimed tool — the distinction is diagnostic
/// only. <b><c>AddRedbRouteLlm()</c> registers the default implementation,
/// <c>ExchangePrincipalClaimsSource</c></b>, which reads the caller from the exchange; a host with its
/// own vocabulary registers its own source instead (it wins). Removing the default leaves
/// claim-declaring tools unbuildable: that is the fail-closed default, not an oversight.
/// </para>
/// <para>
/// Claim names are scope-like strings, matching the vocabulary the ecosystem already issues for
/// authorization scopes: <c>redb.Identity</c> hands out values such as <c>identity:scopes:read</c> /
/// <c>identity:scopes:write</c> (<c>IdentityScopes</c>) and puts them in the token's <c>scope</c>
/// claim. Which source feeds this interface — and therefore which exact names a tool may require —
/// is a deployment decision, not something this contract fixes; the engine only compares strings.
/// </para>
/// </summary>
public interface IToolClaimsSource
{
    /// <summary>
    /// Returns the claims held by the principal owning <paramref name="exchange"/>, or <c>null</c>
    /// when the run has no verifiable principal.
    /// </summary>
    /// <param name="exchange">The agent run's exchange — carries the DI scope the host resolves its
    /// identity/security services from.</param>
    IReadOnlyCollection<string>? GetClaims(IExchange exchange);
}

/// <summary>
/// Claims source backed by a delegate — the seam for deployments that resolve claims from their own
/// identity layer, and for tests.
/// </summary>
public sealed class DelegateToolClaimsSource : IToolClaimsSource
{
    private readonly Func<IExchange, IReadOnlyCollection<string>?> _resolve;

    /// <summary>Creates a source over <paramref name="resolve"/>.</summary>
    /// <param name="resolve">Delegate returning the claims (or <c>null</c> for "no verifiable principal").</param>
    public DelegateToolClaimsSource(Func<IExchange, IReadOnlyCollection<string>?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string>? GetClaims(IExchange exchange) => _resolve(exchange);
}
