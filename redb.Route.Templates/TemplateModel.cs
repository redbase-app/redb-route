using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Expressions;
using Scriban.Runtime;

namespace redb.Route.Templates;

/// <summary>
/// The data a template sees — one model for the DSL and the XML form:
/// <c>body</c> (JSON text parsed to an object tree, XML text to a navigable tree, a POCO as-is with
/// its public members), <c>headers.name</c> / <c>headers["Content-Type"]</c>, <c>properties.name</c>,
/// <c>exception.message</c>, <c>args.name</c>, and two functions: <c>expr("header.a * 2")</c>
/// (the route expression language, same engine as <c>${...}</c>) and <c>raw</c> (skip escaping).
/// No <c>exchange</c>, no context, no methods (README decision 4).
/// </summary>
internal static class TemplateModel
{
    public static ScriptObject Build(IExchange exchange, IReadOnlyDictionary<string, object?> args)
    {
        var model = new ScriptObject
        {
            ["body"] = ConvertBody(exchange.In.Body, exchange.In.ContentType),
            ["headers"] = ToScriptObject(exchange.In.Headers),
            // Framework bookkeeping (__redb_scope:* holds live DI scopes) is not data; same rule as JsonTransform.
            ["properties"] = ToScriptObject(exchange.Properties.Where(p => !p.Key.StartsWith("__", StringComparison.Ordinal))),
            ["exception"] = exchange.Exception is { } ex
                ? new ScriptObject { ["message"] = ex.Message, ["type"] = ex.GetType().Name, ["stackTrace"] = ex.StackTrace }
                : null,
            ["args"] = ToScriptObject(args),
        };
        model.Import("expr", new Func<string, object?>(expression => ExpressionResolver.ResolveExpression(expression, exchange)));
        model.Import("raw", new Func<object?, RawString>(value => new RawString(value?.ToString())));
        return model;
    }

    /// <summary>JSON / XML text (by content type or first significant character) becomes a tree; anything else is passed through.</summary>
    internal static object? ConvertBody(object? body, string? contentType)
    {
        switch (body)
        {
            case null:
                return null;
            case byte[] bytes:
                return ConvertBody(Encoding.UTF8.GetString(bytes), contentType);
            case JsonNode node:
                return FromJson(node);
            case JsonElement element:
                return FromJson(JsonSerializer.SerializeToNode(element));
            case JsonDocument document:
                return FromJson(JsonSerializer.SerializeToNode(document.RootElement));
            case XDocument xdoc:
                return xdoc.Root is null ? null : new ScriptObject { [xdoc.Root.Name.LocalName] = FromXml(xdoc.Root) };
            case XElement xel:
                return new ScriptObject { [xel.Name.LocalName] = FromXml(xel) };
            case string text:
                return ConvertText(text, contentType);
            case IDictionary<string, object?> dict:
                return ToScriptObject(dict);
            default:
                return body;
        }
    }

    private static object? ConvertText(string text, string? contentType)
    {
        var trimmed = text.AsSpan().TrimStart();
        var isJson = contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
            || (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['));
        if (isJson)
        {
            try { return FromJson(JsonNode.Parse(text)); }
            catch (JsonException) { return text; }
        }

        var isXml = contentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true
            || (trimmed.Length > 0 && trimmed[0] == '<');
        if (isXml)
        {
            try
            {
                var root = XDocument.Parse(text).Root;
                return root is null ? text : new ScriptObject { [root.Name.LocalName] = FromXml(root) };
            }
            catch (System.Xml.XmlException) { return text; }
        }

        return text;
    }

    private static object? FromJson(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => ObjectFromJson(obj),
        JsonArray arr => ArrayFromJson(arr),
        JsonValue value => ScalarFromJson(value),
        _ => node.ToJsonString(),
    };

    private static ScriptObject ObjectFromJson(JsonObject obj)
    {
        var result = new ScriptObject();
        foreach (var (key, value) in obj)
            result[key] = FromJson(value);
        return result;
    }

    private static ScriptArray ArrayFromJson(JsonArray arr)
    {
        var result = new ScriptArray();
        foreach (var item in arr)
            result.Add(FromJson(item));
        return result;
    }

    private static object? ScalarFromJson(JsonValue value)
    {
        var element = value.GetValue<JsonElement>();
        switch (element.ValueKind)
        {
            case JsonValueKind.String: return element.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var i)) return i;
                if (element.TryGetInt64(out var l)) return l;
                if (element.TryGetDecimal(out var m)) return m;
                return element.GetDouble();
            default: return element.GetRawText();
        }
    }

    /// <summary>
    /// Element → object: attributes and child elements become members (repeated child names become an
    /// array); a leaf element becomes its text; an element with children keeps its own text under
    /// <c>text</c>. So <c>&lt;order id="7"&gt;&lt;item sku="a"/&gt;&lt;item sku="b"/&gt;&lt;/order&gt;</c> reads as
    /// <c>body.order.id</c>, <c>body.order.item[1].sku</c>.
    /// </summary>
    private static object? FromXml(XElement element)
    {
        if (!element.HasElements && !element.HasAttributes)
            return element.Value;

        var result = new ScriptObject();
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            result[attribute.Name.LocalName] = attribute.Value;

        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName))
        {
            var items = group.ToList();
            if (items.Count == 1)
                result[group.Key] = FromXml(items[0]);
            else
            {
                var array = new ScriptArray();
                foreach (var item in items) array.Add(FromXml(item));
                result[group.Key] = array;
            }
        }

        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (text.Length > 0)
            result["text"] = text;
        return result;
    }

    private static ScriptObject ToScriptObject(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var result = new ScriptObject();
        foreach (var (key, value) in pairs)
            result[key] = value is IDictionary<string, object?> nested ? ToScriptObject(nested) : value;
        return result;
    }
}
