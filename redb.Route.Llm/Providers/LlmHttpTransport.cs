using System.Net;

namespace redb.Route.Llm.Providers;

/// <summary>
/// The HTTP client every model call of this package goes through when the caller does not bring its own.
/// <para>
/// A non-streaming call is one request whose answer arrives only when the model has finished: tens of seconds,
/// minutes for a thinking model or a long recording, with nothing crossing the wire. VPN tunnels, NAT and proxies
/// drop a TLS connection that stays silent for about 50 seconds, and the caller then sees "The response ended
/// prematurely" at that mark; a retry is just as long. The client asks for HTTP/2 (HTTP/1.1 stays the fallback) and
/// sends a PING frame every 15 seconds while a request is in flight, which keeps the connection visibly alive
/// without touching the request. Over HTTP/1.1, and on a plain <c>http://</c> endpoint, no pings are sent and nothing
/// changes. Measured 2026-09-04 through a VPN tunnel: 45 s of silence survived, 55 s was cut; with a ping every
/// 15 s four consecutive 50-second waits all completed.
/// </para>
/// <para>
/// The MCP transport keeps a copy of <see cref="BuildHandler"/>: that package does not reference this one.
/// </para>
/// </summary>
internal static class LlmHttpTransport
{
    /// <summary>The handler: HTTP/2 keep-alive pings every 15 seconds while a request is in flight.</summary>
    internal static SocketsHttpHandler BuildHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        KeepAlivePingDelay = TimeSpan.FromSeconds(15),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
    };

    /// <summary>
    /// A client on <see cref="BuildHandler"/> that asks for HTTP/2. It has no timeout of its own: the factory's
    /// <see cref="LlmConnectionFactory.RequestTimeoutMs"/> is applied per call (<see cref="WithinCallLimitAsync{T}"/>).
    /// </summary>
    internal static HttpClient BuildClient(LlmConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new HttpClient(BuildHandler())
        {
            // HttpClient.Timeout stops at the response headers under ResponseHeadersRead, which every provider
            // uses; it cannot be the limit of a call whose body arrives minutes after the headers.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            // HTTP/2 when the server offers it, so the keep-alive pings have a frame to ride on.
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    /// <summary>The factory's limit on one call: <see cref="LlmConnectionFactory.RequestTimeoutMs"/>, at least a second.</summary>
    internal static TimeSpan CallLimit(LlmConnectionFactory factory) =>
        TimeSpan.FromMilliseconds(Math.Max(1000, factory.RequestTimeoutMs));

    /// <summary>
    /// Runs one non-streaming call within the factory's limit: waiting for the response and reading it, whatever
    /// client sends it. The client's own <see cref="HttpClient.Timeout"/> cannot do this: with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> it stops at the headers, and a server that sends them at
    /// once and holds the body until the model has finished (DeepSeek does) was never limited at all. Running out
    /// throws <see cref="LlmTimeoutException"/>; a cancellation by the caller stays an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    internal static async Task<T> WithinCallLimitAsync<T>(
        LlmConnectionFactory factory, string providerId, Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        var limit = CallLimit(factory);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        try
        {
            return await call(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new LlmTimeoutException(providerId, factory.Name, limit, LlmTimeoutKind.Call,
                $"{providerId}: the call did not finish within RequestTimeoutMs = {limit.TotalMilliseconds:0} ms"
                + $"{OfFactory(factory)}. The limit covers waiting for the answer and reading it; a long generation "
                + "needs a larger RequestTimeoutMs.", ex);
        }
    }

    private static string OfFactory(LlmConnectionFactory factory) =>
        string.IsNullOrEmpty(factory.Name) ? string.Empty : $" (factory '{factory.Name}')";

    /// <summary>
    /// A request that carries the client's HTTP version. <see cref="HttpClient.DefaultRequestVersion"/> applies only to
    /// messages the client creates itself; a hand-built message starts at HTTP/1.1, and then the pings never happen.
    /// </summary>
    internal static HttpRequestMessage NewRequest(HttpClient client, HttpMethod method, Uri uri) => new(method, uri)
    {
        Version = client.DefaultRequestVersion,
        VersionPolicy = client.DefaultVersionPolicy,
    };
}
