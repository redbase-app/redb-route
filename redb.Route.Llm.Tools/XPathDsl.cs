using redb.Route.Abstractions;
using redb.Route.Core;
using FrameworkXPathExpression = redb.Route.Expressions.XPathExpression;

namespace redb.Route.Llm.Tools;

/// <summary>
/// DSL extension that wires an XPath query into a route step. Reads
/// <c>{"xml":"&lt;document&gt;","xpath":"//book[1]/title"}</c> from
/// <c>exchange.In.Body</c>, evaluates the path against the embedded document
/// using <see cref="FrameworkXPathExpression"/> (W3C XPath 1.0 — same engine
/// the rest of redb.Route uses for <c>xpath(...)</c> in routes) and writes
/// the matched value as a string — or the literal <c>null</c> when nothing
/// matched — to <c>exchange.Out.Body</c>.
/// <example>
/// <code>
/// From("direct:llm.xpath")
///     .AsLlmTool("xpath").Description("Query an XML document.").Then()
///     .XPath(new XPathOptions());
/// </code>
/// </example>
/// </summary>
public static class XPathDsl
{
    /// <summary>Adds an XPath query step. Uses default options when <paramref name="options"/> is null.</summary>
    public static IRouteDefinition XPath(this IRouteDefinition self, XPathOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        options ??= new XPathOptions();

        return self.Process((exchange, _) =>
        {
            var (xml, xpath) = ParseInput(exchange.In.Body);
            if (xml.Length > options.MaxXmlChars)
                throw new ArgumentException(
                    $"Input xml exceeds MaxXmlChars ({options.MaxXmlChars}); got {xml.Length}.");

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
        });
    }

    private static (string Xml, string XPath) ParseInput(object? body)
    {
        using var doc = LlmToolJson.ParseObject(body, "XPath");
        return (
            LlmToolJson.RequiredString(doc.RootElement, "xml", "XPath"),
            LlmToolJson.OptionalString(doc.RootElement, "xpath") ?? "/");
    }
}
