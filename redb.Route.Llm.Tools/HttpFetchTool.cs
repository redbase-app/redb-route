using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Reference HTTP-fetch tool for redb.Route.Llm. Mounts a route on
/// <see cref="HttpFetchOptions.EndpointUri"/> and registers it as an
/// <see cref="ILlmToolDescriptor"/> via <c>.AsLlmTool(...)</c>. Built on
/// <see cref="HttpClient"/> directly \u2014 does not depend on
/// <c>redb.Route.Http</c>.
/// <para>
/// Input schema: <c>{"url": "https://..."}</c>. Response body is returned as
/// text in <c>exchange.Out.Body</c>; bytes past <see cref="HttpFetchOptions.MaxBytes"/>
/// are truncated.
/// </para>
/// <example>
/// <code>
/// context.AddRoutes(new HttpFetchTool(new HttpFetchOptions
/// {
///     HostAllowlist = ["api.example.com"],
///     MaxBytes = 1_000_000,
///     Timeout = TimeSpan.FromSeconds(15)
/// }));
/// </code>
/// </example>
/// </summary>
public sealed class HttpFetchTool : RouteBuilder
{
    private static readonly HttpClient SharedClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "redb.Route.Llm/HttpFetchTool" } }
    };

    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "Absolute http(s) URL to fetch." }
          },
          "required": ["url"],
          "additionalProperties": false
        }
        """;

    private readonly HttpFetchOptions _options;

    /// <summary>Creates the tool with the given options.</summary>
    public HttpFetchTool(HttpFetchOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override void Configure()
    {
        var processor = new HttpFetchProcessor(SharedClient, _options);

        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Fetches a URL via HTTP GET and returns the response body as UTF-8 text.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.External)
                .Cost(ToolCostClass.Moderate)
            .Then()
            .Process(processor);
    }

    private sealed class HttpFetchProcessor : IProcessor
    {
        private readonly HttpClient _client;
        private readonly HttpFetchOptions _options;

        public HttpFetchProcessor(HttpClient client, HttpFetchOptions options)
        {
            _client = client;
            _options = options;
        }

        public async Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var url = ExtractUrl(exchange.In.Body);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException($"Invalid URL '{url}' \u2014 expected absolute http(s).");

            if (_options.HostAllowlist.Count > 0 &&
                !_options.HostAllowlist.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Host '{uri.Host}' is not in the allowlist for tool '{_options.ToolName}'.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var (text, truncated) = await ReadBodyAsync(response, _options.MaxBytes, timeoutCts.Token)
                .ConfigureAwait(false);

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = text;
            exchange.Out.Headers["llm.http_fetch.status"] = (int)response.StatusCode;
            exchange.Out.Headers["llm.http_fetch.bytes"] = text.Length;
            if (truncated)
                exchange.Out.Headers["llm.http_fetch.truncated"] = true;
        }

        private static string ExtractUrl(object? body) => body switch
        {
            null => throw new ArgumentException("HttpFetch input is empty \u2014 expected JSON {\"url\":\"...\"}."),
            string s => ParseUrlFromJson(s),
            _ => ParseUrlFromJson(body.ToString() ?? string.Empty)
        };

        private static string ParseUrlFromJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("url", out var u) &&
                    u.ValueKind == JsonValueKind.String)
                {
                    return u.GetString() ?? string.Empty;
                }
            }
            catch (JsonException) { /* fall through */ }

            throw new ArgumentException("HttpFetch input must be JSON of shape {\"url\":\"...\"}.");
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
}
