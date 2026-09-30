using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.Http;

/// <summary>
/// The inbound check of an http: consumer (<c>inboundAuth=basic|bearer</c>). Runs before the exchange exists: a request
/// it refuses gets 401 with <c>WWW-Authenticate</c> and never reaches the route.
/// </summary>
internal sealed class HttpInboundAuthenticator
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpAuthScheme _scheme;
    private readonly string _realm;
    private readonly byte[]? _userHash;
    private readonly byte[]? _passwordHash;
    private readonly IHttpTokenValidator? _validator;

    private HttpInboundAuthenticator(HttpAuthScheme scheme, string realm, string? user, string? password,
        IHttpTokenValidator? validator)
    {
        _scheme = scheme;
        _realm = realm;
        // Hashes of equal length, so the comparison below takes the same time whatever the input.
        _userHash = user is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(user));
        _passwordHash = password is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(password));
        _validator = validator;
    }

    /// <summary>
    /// The authenticator the options declare, or <c>null</c> for <c>inboundAuth=none</c>. The token validator is resolved
    /// here, while the route starts, so a misspelt name stops the start instead of answering every request with 500.
    /// </summary>
    public static HttpInboundAuthenticator? Create(HttpEndpointOptions options, IRouteContext? context) => options.InboundAuth switch
    {
        HttpAuthScheme.Basic => new(HttpAuthScheme.Basic, options.InboundRealm, options.InboundUsername, options.InboundPassword, null),
        HttpAuthScheme.Bearer => new(HttpAuthScheme.Bearer, options.InboundRealm, null, null,
            context.GetRequiredFromRegistry<IHttpTokenValidator>(
                options.TokenValidator!.StartsWith('#') ? options.TokenValidator[1..] : options.TokenValidator)),
        _ => null,
    };

    /// <summary>
    /// The principal of an accepted request; <c>null</c> after writing the 401 of a refused one.
    /// </summary>
    public async Task<ClaimsPrincipal?> AuthenticateAsync(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers.Authorization;
        var credentials = header.Count == 1 ? Credentials(header[0]) : null;

        if (_scheme == HttpAuthScheme.Basic)
        {
            var principal = credentials is null ? null : CheckBasic(credentials);
            if (principal is null)
                Refuse(httpContext, $"Basic realm=\"{_realm}\", charset=\"UTF-8\"");
            return principal;
        }

        if (credentials is null)
        {
            // No token at all: RFC 6750 §3.1, the challenge carries no error code.
            Refuse(httpContext, $"Bearer realm=\"{_realm}\"");
            return null;
        }

        var accepted = await _validator!.ValidateAsync(credentials, httpContext.RequestAborted).ConfigureAwait(false);
        if (accepted?.Identity?.IsAuthenticated == true)
            return accepted;

        Refuse(httpContext, $"Bearer realm=\"{_realm}\", error=\"invalid_token\"");
        return null;
    }

    /// <summary>The credentials after this authenticator's scheme name, or <c>null</c> when the header is another scheme.</summary>
    private string? Credentials(string? header)
    {
        if (string.IsNullOrEmpty(header))
            return null;
        var space = header.IndexOf(' ');
        if (space <= 0)
            return null;
        var scheme = _scheme == HttpAuthScheme.Basic ? "Basic" : "Bearer";
        if (!header.AsSpan(0, space).Equals(scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        var credentials = header[(space + 1)..].Trim();
        return credentials.Length == 0 ? null : credentials;
    }

    private ClaimsPrincipal? CheckBasic(string encoded)
    {
        var buffer = new byte[encoded.Length];
        if (!Convert.TryFromBase64String(encoded, buffer, out var length))
            return null;

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(buffer, 0, length);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8 (RFC 7617 charset): not credentials this endpoint issued.
            return null;
        }

        var colon = decoded.IndexOf(':');
        if (colon < 0)
            return null;
        var user = decoded[..colon];
        var password = decoded[(colon + 1)..];

        // Both halves are always compared, so the time does not tell which one was wrong.
        var userMatches = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(user)), _userHash);
        var passwordMatches = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(password)), _passwordHash);
        if (!(userMatches & passwordMatches))
            return null;

        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], authenticationType: "Basic"));
    }

    private static void Refuse(HttpContext httpContext, string challenge)
    {
        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        httpContext.Response.Headers[HeaderNames.WWWAuthenticate] = challenge;
    }
}
