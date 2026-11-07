using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm.Tools;

/// <summary>
/// DSL extension that wires a Tavily (https://tavily.com/) web-search call
/// into a route step. Reads <c>{"query":"...","max_results":int?}</c> from
/// <c>exchange.In.Body</c>, calls the Tavily Search API with the API key
/// configured in <see cref="TavilyWebSearchOptions"/> and writes the shaped
/// response — <c>{"answer":"...?", "results":[{"title","url","content"}]}</c>
/// — to <c>exchange.Out.Body</c> as a JSON string.
/// <example>
/// <code>
/// From("direct:llm.web_search")
///     .AsLlmTool("web_search").Description("Search the web via Tavily.").Then()
///     .TavilyWebSearch(new TavilyWebSearchOptions { ApiKey = apiKey });
/// </code>
/// </example>
/// </summary>
public static class TavilyWebSearchDsl
{
    private static readonly HttpClient SharedClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "redb.Route.Llm/TavilyWebSearch" } }
    };

    /// <summary>Adds a Tavily web-search step. <see cref="TavilyWebSearchOptions.ApiKey"/> is required.</summary>
    public static IRouteDefinition TavilyWebSearch(this IRouteDefinition self, TavilyWebSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("TavilyWebSearchOptions.ApiKey is required.", nameof(options));

        return self.Process(async (exchange, ct) =>
        {
            var (query, maxResults) = ParseInput(exchange.In.Body);
            var effectiveMax = Math.Clamp(maxResults ?? options.MaxResults, 1, 20);

            var requestBody = new JsonObject
            {
                ["api_key"] = options.ApiKey,
                ["query"] = query,
                ["max_results"] = effectiveMax,
                ["search_depth"] = options.SearchDepth,
                ["include_answer"] = options.IncludeAnswer
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
            };

            using var response = await SharedClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Tavily search failed with HTTP {(int)response.StatusCode}: {Truncate(raw, 512)}");

            var output = ShapeOutput(raw);
            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = output.ToJsonString();
            exchange.Out.Headers["llm.web_search.results"] =
                (output["results"] as JsonArray)?.Count ?? 0;
        });
    }

    private static (string Query, int? MaxResults) ParseInput(object? body)
    {
        using var doc = LlmToolJson.ParseObject(body, "TavilyWebSearch");
        return (
            LlmToolJson.RequiredString(doc.RootElement, "query", "TavilyWebSearch"),
            LlmToolJson.OptionalInt(doc.RootElement, "max_results"));
    }

    private static JsonObject ShapeOutput(string raw)
    {
        var node = JsonNode.Parse(raw) as JsonObject
                   ?? throw new InvalidOperationException("Tavily returned a non-object response.");

        var shaped = new JsonObject();
        if (node["answer"] is JsonValue answerValue && answerValue.GetValueKind() == JsonValueKind.String)
            shaped["answer"] = answerValue.DeepClone();

        var results = new JsonArray();
        if (node["results"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject obj) continue;
                var entry = new JsonObject();
                if (obj["title"] is JsonNode title) entry["title"] = title.DeepClone();
                if (obj["url"] is JsonNode url) entry["url"] = url.DeepClone();
                if (obj["content"] is JsonNode content) entry["content"] = content.DeepClone();
                results.Add(entry);
            }
        }
        shaped["results"] = results;
        return shaped;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
