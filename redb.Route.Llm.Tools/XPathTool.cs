using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;
using FrameworkXPathExpression = redb.Route.Expressions.XPathExpression;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Extracts a value from an XML document via the framework's
/// <see cref="FrameworkXPathExpression"/> — W3C XPath 1.0, same engine the
/// rest of redb.Route uses for <c>XPath(...)</c>/<c>xpath(...)</c> in routes.
/// <para>
/// Input: <c>{"xml":"&lt;root&gt;...&lt;/root&gt;","xpath":"//book[1]/title"}</c>.
/// Output: matched value as a string in <c>exchange.Out.Body</c>, or the
/// literal <c>null</c> when nothing matched.
/// </para>
/// </summary>
public sealed class XPathTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "xml":   { "type": "string", "description": "XML document to query." },
            "xpath": { "type": "string", "description": "XPath 1.0 expression (e.g. '//book[1]/title')." }
          },
          "required": ["xml", "xpath"],
          "additionalProperties": false
        }
        """;

    private readonly XPathOptions _options;

    public XPathTool() : this(new XPathOptions()) { }

    public XPathTool(XPathOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override void Configure()
    {
        var processor = new XPathProcessor(_options);

        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Extracts a value from an XML document via XPath 1.0. Returns the matched " +
                             "value as a string, or null when nothing matches.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .Process(processor);
    }

    private sealed class XPathProcessor : IProcessor
    {
        private readonly XPathOptions _options;

        public XPathProcessor(XPathOptions options) => _options = options;

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var (xml, xpath) = ParseInput(exchange.In.Body);

            if (xml.Length > _options.MaxXmlChars)
                throw new ArgumentException(
                    $"Input xml exceeds MaxXmlChars ({_options.MaxXmlChars}); got {xml.Length}.");

            var child = exchange.CreateChild(new Message(xml));
            string? value;
            try
            {
                value = new FrameworkXPathExpression(xpath).Evaluate<string?>(child);
            }
            catch (InvalidOperationException)
            {
                value = null;
            }

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = value is null ? "null" : value;
            exchange.Out.Headers["llm.xpath.matched"] = value is not null;
            return Task.CompletedTask;
        }

        private static (string Xml, string XPath) ParseInput(object? body)
        {
            if (body is null)
                throw new ArgumentException("XPath input is empty — expected JSON {\"xml\":\"...\",\"xpath\":\"...\"}.");

            var raw = body as string ?? body.ToString() ?? string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("XPath input must be an object.");

                if (!doc.RootElement.TryGetProperty("xml", out var x) || x.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("XPath input must include 'xml' as a string.");
                if (!doc.RootElement.TryGetProperty("xpath", out var p) || p.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("XPath input must include 'xpath' as a string.");

                return (x.GetString() ?? string.Empty, p.GetString() ?? "/");
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("XPath input is not valid JSON.", ex);
            }
        }
    }
}
