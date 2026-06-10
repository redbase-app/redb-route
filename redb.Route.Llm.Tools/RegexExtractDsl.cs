using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm.Tools;

/// <summary>
/// DSL extension that wires a regex-extraction step into a route. Reads
/// <c>{"text":"...","pattern":"...","group":"name|number","all":bool}</c>
/// from <c>exchange.In.Body</c>, runs the pattern under
/// <see cref="RegexExtractOptions.MatchTimeout"/> (default 1 s — guards
/// against catastrophic backtracking) and writes the result to
/// <c>exchange.Out.Body</c> as JSON: a string, an array of strings, or
/// <c>null</c> when nothing matched.
/// <example>
/// <code>
/// From("direct:llm.regex_extract")
///     .AsLlmTool("regex_extract").Description("Extract text by a .NET regex.").Then()
///     .RegexExtract(new RegexExtractOptions());
/// </code>
/// </example>
/// </summary>
public static class RegexExtractDsl
{
    /// <summary>Adds a regex extraction step. Uses default options when <paramref name="options"/> is null.</summary>
    public static IRouteDefinition RegexExtract(this IRouteDefinition self, RegexExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        options ??= new RegexExtractOptions();

        return self.Process((exchange, _) =>
        {
            var input = ParseInput(exchange.In.Body);
            if (input.Text.Length > options.MaxTextChars)
                throw new ArgumentException(
                    $"Input text exceeds MaxTextChars ({options.MaxTextChars}); got {input.Text.Length}.");

            Regex regex;
            try
            {
                regex = new Regex(input.Pattern, RegexOptions.CultureInvariant, options.MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid regex pattern: {ex.Message}", ex);
            }

            JsonNode? result;
            if (input.All)
            {
                var arr = new JsonArray();
                var count = 0;
                foreach (Match m in regex.Matches(input.Text))
                {
                    if (count >= options.MaxMatches) break;
                    var v = SelectGroup(m, input.Group);
                    if (v is not null) arr.Add(v);
                    count++;
                }
                result = arr.Count == 0 ? null : arr;
            }
            else
            {
                var m = regex.Match(input.Text);
                result = m.Success ? SelectGroup(m, input.Group) : null;
            }

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = result is null ? "null" : result.ToJsonString();
            exchange.Out.Headers["llm.regex_extract.matched"] = result is not null;
            return Task.CompletedTask;
        });
    }

    private static string? SelectGroup(Match m, string? group)
    {
        if (string.IsNullOrEmpty(group) || group == "0")
            return m.Value;

        if (int.TryParse(group, out var idx))
            return idx < m.Groups.Count && m.Groups[idx].Success ? m.Groups[idx].Value : null;

        var named = m.Groups[group];
        return named.Success ? named.Value : null;
    }

    private static (string Text, string Pattern, string? Group, bool All) ParseInput(object? body)
    {
        using var doc = LlmToolJson.ParseObject(body, "RegexExtract");
        var root = doc.RootElement;
        return (
            LlmToolJson.RequiredString(root, "text", "RegexExtract"),
            LlmToolJson.RequiredString(root, "pattern", "RegexExtract"),
            LlmToolJson.OptionalString(root, "group"),
            LlmToolJson.OptionalBool(root, "all"));
    }
}
