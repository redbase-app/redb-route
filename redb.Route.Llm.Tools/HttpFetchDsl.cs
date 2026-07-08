using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm.Tools;

/// <summary>
/// DSL extension that wires an HTTP-GET fetcher into a route step. Reads
/// <c>{"url":"..."}</c> from <c>exchange.In.Body</c>, fetches the URL with the
/// host / max-bytes / timeout guards configured in
/// <see cref="HttpFetchOptions"/>, and writes the response body (UTF-8 text)
/// to <c>exchange.Out.Body</c>.
/// <para>
/// Built on <see cref="HttpClient"/> directly — does not depend on
/// <c>redb.Route.Http</c>. Reuses one shared client across all callers.
/// </para>
/// <example>
/// <code>
/// // Canonical usage on a tool route:
/// From("direct:fetch-weather")
///     .AsLlmTool("get_weather")
///         .Description("Fetches the latest weather for a URL.")
///         .Input("""{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""")
///     .Then()
///     .HttpFetch(new HttpFetchOptions { HostAllowlist = ["api.weather.gov"] });
///
/// // Or as a plain enrichment step in any pipeline:
/// From("amqp:queue:urls").HttpFetch(opts).To("amqp:queue:fetched");
/// </code>
/// </example>
/// </summary>
public static class HttpFetchDsl
{
    private static readonly HttpClient SharedClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "redb.Route.Llm/HttpFetch" } }
    };

    /// <summary>Adds an HTTP-GET fetcher step that reads <c>{"url":"..."}</c> from the body.</summary>
    public static IRouteDefinition HttpFetch(this IRouteDefinition self, HttpFetchOptions options)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(options);

        return self.Process(async (exchange, ct) =>
        {
            var url = ExtractUrl(exchange.In.Body);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException($"Invalid URL '{url}' — expected absolute http(s).");

            if (options.HostAllowlist.Count > 0 &&
                !options.HostAllowlist.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Host '{uri.Host}' is not in the allowlist for '{options.ToolName}'.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await SharedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var (text, truncated) = await ReadBodyAsync(response, options.MaxBytes, timeoutCts.Token)
                .ConfigureAwait(false);

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = text;
            exchange.Out.Headers["llm.http_fetch.status"] = (int)response.StatusCode;
            exchange.Out.Headers["llm.http_fetch.bytes"] = text.Length;
            if (truncated)
                exchange.Out.Headers["llm.http_fetch.truncated"] = true;
        });
    }

    private static string ExtractUrl(object? body)
    {
        using var doc = LlmToolJson.ParseObject(body, "HttpFetch");
        return LlmToolJson.RequiredString(doc.RootElement, "url", "HttpFetch");
    }

    private static async Task<(string Text, bool Truncated)> ReadBodyAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[Math.Min(maxBytes, 16 * 1024)];
        using var ms = new MemoryStream();
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes)
            {
                var remaining = maxBytes - (int)ms.Length;
                if (remaining > 0) ms.Write(buffer, 0, remaining);
                truncated = true;
                break;
            }
            ms.Write(buffer, 0, read);
        }
        return (Encoding.UTF8.GetString(ms.ToArray()), truncated);
    }
}
