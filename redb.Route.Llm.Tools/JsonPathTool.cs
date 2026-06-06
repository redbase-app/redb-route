using System.Linq;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Extracts a value from a JSON document via Newtonsoft <c>JToken.SelectToken</c>
/// / <c>SelectTokens</c> — the same engine that backs the framework's
/// <see cref="redb.Route.Expressions.JsonPathExpression"/>. The full Newtonsoft
/// JsonPath dialect is supported: recursive descent (<c>..</c>), wildcards
/// (<c>[*]</c>), filters (<c>[?(@.foo == 1)]</c>), slicing (<c>[1:5]</c>),
/// plus the simple property/index forms.
/// <para>
/// Input: <c>{"json":"&lt;document&gt;","path":"$.foo[0].bar"}</c>.
/// Output: matched value re-serialised as JSON, or the literal <c>null</c>
/// when nothing matched.
/// </para>
/// <para>
/// Direct <c>JToken</c> use (rather than wrapping <see cref="redb.Route.Expressions.JsonPathExpression"/>)
/// is deliberate — the framework expression's typed-conversion shim is built
/// for route property extraction and trips on the <c>JValue → JToken</c> path
/// for primitives. The engine choice (Newtonsoft) stays aligned with the rest
/// of redb.Route.
/// </para>
/// </summary>
public sealed class JsonPathTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "json": { "type": "string", "description": "JSON document to query." },
            "path": { "type": "string", "description": "JsonPath expression (full Newtonsoft dialect)." }
          },
          "required": ["json", "path"],
          "additionalProperties": false
        }
        """;

    private readonly JsonPathOptions _options;

    public JsonPathTool() : this(new JsonPathOptions()) { }

    public JsonPathTool(JsonPathOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override void Configure()
    {
        var processor = new JsonPathProcessor(_options);

        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Extracts a value from a JSON document via JsonPath (Newtonsoft dialect: " +
                             "recursive '..', wildcards '[*]', filters '[?...]', slices). Returns the matched " +
                             "value as JSON, or null when nothing matches.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .Process(processor);
    }

    private sealed class JsonPathProcessor : IProcessor
    {
        private readonly JsonPathOptions _options;

        public JsonPathProcessor(JsonPathOptions options) => _options = options;

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var (json, path) = ParseInput(exchange.In.Body);

            if (json.Length > _options.MaxJsonChars)
                throw new ArgumentException(
                    $"Input json exceeds MaxJsonChars ({_options.MaxJsonChars}); got {json.Length}.");

            // Use Newtonsoft directly — same engine that backs the framework's
            // JsonPathExpression, but without the typed-conversion shim it does
            // for in-route property extraction (which trips on JValue→JToken).
            JToken? token;
            try
            {
                var root = JToken.Parse(json);
                if (path.Contains("..") || path.Contains("[?") || path.Contains("[*]") ||
                    System.Text.RegularExpressions.Regex.IsMatch(path, @"\[\d+:\d+\]"))
                {
                    var matches = root.SelectTokens(path).ToArray();
                    token = matches.Length == 0 ? null : new JArray(matches.Cast<object>().ToArray());
                }
                else
                {
                    token = root.SelectToken(path);
                }
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new ArgumentException($"JsonPath evaluation failed: {ex.Message}", ex);
            }

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = token is null ? "null" : token.ToString(Newtonsoft.Json.Formatting.None);
            exchange.Out.Headers["llm.json_path.matched"] = token is not null;
            return Task.CompletedTask;
        }

        private static (string Json, string Path) ParseInput(object? body)
        {
            if (body is null)
                throw new ArgumentException("JsonPath input is empty — expected JSON {\"json\":\"...\",\"path\":\"...\"}.");

            var raw = body as string ?? body.ToString() ?? string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("JsonPath input must be an object.");

                if (!doc.RootElement.TryGetProperty("json", out var j) || j.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("JsonPath input must include 'json' as a string.");
                if (!doc.RootElement.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("JsonPath input must include 'path' as a string.");

                return (j.GetString() ?? string.Empty, p.GetString() ?? "$");
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("JsonPath input is not valid JSON.", ex);
            }
        }
    }
}
