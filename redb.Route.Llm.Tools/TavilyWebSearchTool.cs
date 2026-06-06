using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Web search tool backed by the Tavily API (https://tavily.com/).
/// <para>
/// Input: <c>{"query":"...","max_results":5}</c> — <c>max_results</c> optional, defaults
/// from <see cref="TavilyWebSearchOptions.MaxResults"/>.
/// </para>
/// <para>
/// Output: JSON of shape <c>{"answer":"...","results":[{"title":"...","url":"...","content":"..."}]}</c>.
/// The <c>answer</c> field is omitted when <see cref="TavilyWebSearchOptions.IncludeAnswer"/> is false
/// or Tavily did not return one.
/// </para>
/// </summary>
public sealed class TavilyWebSearchTool : RouteBuilder
{
    private static readonly HttpClient SharedClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "redb.Route.Llm/TavilyWebSearchTool" } }
    };

    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "query":       { "type": "string", "description": "Web search query." },
            "max_results": { "type": "integer", "description": "Override default max results (1-20)." }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    private readonly TavilyWebSearchOptions _options;

    public TavilyWebSearchTool(TavilyWebSearchOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ArgumentException("TavilyWebSearchOptions.ApiKey is required.", nameof(options));
    }

    protected override void Configure()
    {
        var processor = new TavilyProcessor(SharedClient, _options);

        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Searches the web via Tavily. Returns a JSON object with an optional 'answer' " +
                             "summary and a 'results' array of {title, url, content}.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.External)
                .Cost(ToolCostClass.Expensive)
            .Then()
            .Process(processor);
    }

    private sealed class TavilyProcessor : IProcessor
    {
        private readonly HttpClient _client;
        private readonly TavilyWebSearchOptions _options;

        public TavilyProcessor(HttpClient client, TavilyWebSearchOptions options)
        {
            _client = client;
            _options = options;
        }

        public async Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var (query, maxResults) = ParseInput(exchange.In.Body);
            var effectiveMax = maxResults ?? _options.MaxResults;
            if (effectiveMax < 1) effectiveMax = 1;
            if (effectiveMax > 20) effectiveMax = 20;

            var requestBody = new JsonObject
            {
                ["api_key"] = _options.ApiKey,
                ["query"] = query,
                ["max_results"] = effectiveMax,
                ["search_depth"] = _options.SearchDepth,
                ["include_answer"] = _options.IncludeAnswer
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
            };

            using var response = await _client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Tavily search failed with HTTP {(int)response.StatusCode}: {Truncate(raw, 512)}");

            var output = ShapeOutput(raw);
            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = output.ToJsonString();
            exchange.Out.Headers["llm.web_search.results"] =
                (output["results"] as JsonArray)?.Count ?? 0;
        }

        private static (string Query, int? MaxResults) ParseInput(object? body)
        {
            if (body is null)
                throw new ArgumentException("TavilyWebSearch input is empty — expected JSON {\"query\":\"...\"}.");

            var raw = body as string ?? body.ToString() ?? string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("TavilyWebSearch input must be an object.");
                if (!doc.RootElement.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("TavilyWebSearch input must include 'query' as a string.");

                int? maxResults = null;
                if (doc.RootElement.TryGetProperty("max_results", out var m) && m.ValueKind == JsonValueKind.Number)
                    maxResults = m.GetInt32();

                return (q.GetString() ?? string.Empty, maxResults);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("TavilyWebSearch input is not valid JSON.", ex);
            }
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
}
