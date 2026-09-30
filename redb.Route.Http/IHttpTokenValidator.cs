using System.Security.Claims;

namespace redb.Route.Http;

/// <summary>
/// Checks the bearer token of an inbound request for an http: consumer or a REST declaration with
/// <c>inboundAuth=bearer</c>, registered in the route registry and named by <c>tokenValidator=#name</c>.
/// <para>
/// The connector does not read tokens itself: JWT parsing, signing keys and their rotation, audiences and scopes belong
/// to the identity provider's library (redb.Identity, or any OpenID Connect client). This interface is where it plugs in.
/// </para>
/// </summary>
public interface IHttpTokenValidator
{
    /// <summary>
    /// The principal the token stands for, or <c>null</c> when the token is not accepted (the caller gets 401 with
    /// <c>error="invalid_token"</c>). A principal whose identity is not authenticated counts as not accepted. An exception
    /// is a failure of the validator, not of the caller, and answers 500.
    /// </summary>
    /// <param name="token">The token after <c>Bearer </c>, as the client sent it.</param>
    /// <param name="ct">Cancelled when the request is aborted.</param>
    Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken ct);
}
