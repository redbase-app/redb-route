using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Extracts values from text by a .NET regular expression.
/// <para>
/// Input: <c>{"text":"...","pattern":"...","group":"name|number","all":bool}</c>.
/// <list type="bullet">
///   <item><c>group</c> selects a capture group; <c>"0"</c> or omitted = whole match.</item>
///   <item><c>all=true</c> returns a JSON array of every match; <c>false</c> (default) returns the first match only.</item>
/// </list>
/// Output is JSON: a string, or an array of strings, or <c>null</c> when nothing matched.
/// </para>
/// <para>
/// Pattern execution is bounded by <see cref="RegexExtractOptions.MatchTimeout"/>
/// to prevent catastrophic backtracking from a hostile or careless pattern.
/// </para>
/// </summary>
public sealed class RegexExtractTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "text":    { "type": "string", "description": "Text to scan." },
            "pattern": { "type": "string", "description": ".NET regular expression." },
            "group":   { "type": "string", "description": "Capture group name or number. Omit / '0' for the whole match." },
            "all":     { "type": "boolean", "description": "Return every match as an array. Default false." }
          },
          "required": ["text", "pattern"],
          "additionalProperties": false
        }
        """;

    private readonly RegexExtractOptions _options;

    public RegexExtractTool() : this(new RegexExtractOptions()) { }

    public RegexExtractTool(RegexExtractOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override void Configure()
    {
        var processor = new RegexExtractProcessor(_options);

        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Extracts text by a .NET regex. Returns the matched group as JSON " +
                             "(string, array of strings, or null when nothing matches).")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .Process(processor);
    }

    private sealed class RegexExtractProcessor : IProcessor
    {
        private readonly RegexExtractOptions _options;

        public RegexExtractProcessor(RegexExtractOptions options) => _options = options;

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var input = ParseInput(exchange.In.Body);

            if (input.Text.Length > _options.MaxTextChars)
                throw new ArgumentException(
                    $"Input text exceeds MaxTextChars ({_options.MaxTextChars}); got {input.Text.Length}.");

            Regex regex;
            try
            {
                regex = new Regex(input.Pattern, RegexOptions.CultureInvariant, _options.MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid regex pattern: {ex.Message}", ex);
            }

            JsonNode? result;
            if (input.All)
            {
                var arr = new JsonArray();
                int count = 0;
                foreach (Match m in regex.Matches(input.Text))
                {
                    if (count >= _options.MaxMatches) break;
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
            if (body is null)
                throw new ArgumentException("RegexExtract input is empty — expected JSON object.");

            var raw = body as string ?? body.ToString() ?? string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("RegexExtract input must be an object.");

                if (!doc.RootElement.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("RegexExtract input must include 'text' as a string.");
                if (!doc.RootElement.TryGetProperty("pattern", out var p) || p.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("RegexExtract input must include 'pattern' as a string.");

                string? group = null;
                if (doc.RootElement.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String)
                    group = g.GetString();

                var all = doc.RootElement.TryGetProperty("all", out var a)
                    && a.ValueKind == JsonValueKind.True;

                return (t.GetString() ?? string.Empty, p.GetString() ?? string.Empty, group, all);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("RegexExtract input is not valid JSON.", ex);
            }
        }
    }
}
