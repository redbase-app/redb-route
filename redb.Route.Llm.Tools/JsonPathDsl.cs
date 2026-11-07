using System.Linq;
using Newtonsoft.Json.Linq;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm.Tools;

/// <summary>
/// DSL extension that wires a JsonPath query into a route step. Reads
/// <c>{"json":"&lt;document&gt;","path":"$.foo[0].bar"}</c> from
/// <c>exchange.In.Body</c>, evaluates the path against the embedded document
/// and writes the matched value (re-serialised as JSON) — or the literal
/// <c>null</c> when nothing matched — to <c>exchange.Out.Body</c>.
/// <para>
/// Uses Newtonsoft <c>JToken.SelectToken</c> / <c>SelectTokens</c> directly,
/// the same engine that backs
/// <c>redb.Route.Expressions.JsonPathExpression</c> (which is built for
/// in-route property extraction and trips on the <c>JValue → JToken</c> path
/// for primitives). The full Newtonsoft JsonPath dialect is supported:
/// recursive descent (<c>..</c>), wildcards (<c>[*]</c>), filters
/// (<c>[?(@.foo == 1)]</c>), slicing (<c>[1:5]</c>).
/// </para>
/// <example>
/// <code>
/// From("direct:llm.json_path")
///     .AsLlmTool("json_path").Description("Query a JSON document.").Then()
///     .JsonPath(new JsonPathOptions());
/// </code>
/// </example>
/// </summary>
public static class JsonPathDsl
{
    /// <summary>Adds a JsonPath query step. Uses default options when <paramref name="options"/> is null.</summary>
    public static IRouteDefinition JsonPath(this IRouteDefinition self, JsonPathOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        options ??= new JsonPathOptions();

        return self.Process((exchange, _) =>
        {
            var (json, path) = ParseInput(exchange.In.Body);
            if (json.Length > options.MaxJsonChars)
                throw new ArgumentException(
                    $"Input json exceeds MaxJsonChars ({options.MaxJsonChars}); got {json.Length}.");

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
        });
    }

    private static (string Json, string Path) ParseInput(object? body)
    {
        using var doc = LlmToolJson.ParseObject(body, "JsonPath");
        return (
            LlmToolJson.RequiredString(doc.RootElement, "json", "JsonPath"),
            LlmToolJson.OptionalString(doc.RootElement, "path") ?? "$");
    }
}
