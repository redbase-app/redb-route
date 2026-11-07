using System.Globalization;
using System.Xml.Linq;
using System.Xml.XPath;
using redb.Route.Predicates;
using EngineIterator = Wmhelp.XPath2.XPath2NodeIterator;

namespace redb.Route.XPath2;

/// <summary>
/// Turns what the XPath 2.0 engine returns into what a route asked for.
/// </summary>
/// <remarks>
/// XPath 2.0 returns a sequence where 1.0 returned a node-set, and a sequence can hold atomic
/// values as well as nodes — <c>tokenize('a,b', ',')</c> yields two strings that were never in the
/// document. The conversions below therefore work on items rather than on nodes, but land on the
/// same shapes the XPath 1.0 expression produces, so a route can move between the two languages
/// without its surrounding code changing.
/// </remarks>
internal static class XPath2Values
{
    /// <summary>
    /// Reads a result in a condition position.
    /// </summary>
    /// <remarks>
    /// A sequence answers whether anything matched, exactly as a node-set does in XPath 1.0. A
    /// single atomic value is a value like any other and goes through the one DSL truthiness rule,
    /// so <c>"0"</c> is false there — which is the rule this framework applies everywhere, and is
    /// deliberately not XPath 2.0's effective-boolean-value table, whose answer for a lone
    /// <c>"0"</c> string is true.
    /// </remarks>
    internal static bool ToBoolean(object? raw)
    {
        if (raw is not EngineIterator iterator)
            return RouteTruthiness.ToBoolean(raw);

        var items = Drain(iterator);
        return items.Count switch
        {
            0 => false,
            1 when items[0] is not XObject => RouteTruthiness.ToBoolean(items[0]),
            _ => true
        };
    }

    /// <summary>Converts a result to the requested CLR type.</summary>
    internal static T Convert<T>(object? raw, string path, bool trim)
    {
        if (raw is not EngineIterator iterator)
            return ConvertScalar<T>(raw, path, trim);

        var items = Drain(iterator);

        if (items.Count == 0)
        {
            if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
                throw new InvalidOperationException(
                    $"No values found for XPath 2.0 expression '{path}' (expected non-nullable {typeof(T).Name}).");
            return default!;
        }

        return items.Count == 1
            ? ConvertSingle<T>(items[0], path, trim)
            : ConvertMany<T>(items, path, trim);
    }

    /// <summary>
    /// Converts a CLR value into something the engine will bind to a <c>$name</c> variable.
    /// </summary>
    /// <remarks>
    /// Formatting is invariant for the same reason it is everywhere else here: a decimal separator
    /// taken from the server's locale would make one route match different documents on different
    /// machines.
    /// </remarks>
    internal static object ToEngineValue(object? value) => value switch
    {
        null => string.Empty,
        bool or string or int or long or double or DateTime => value,
        decimal m => (double)m,
        float f => (double)f,
        sbyte or byte or short or ushort or uint or ulong
            => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    // ── Items ──

    /// <summary>
    /// Reads a sequence into ordinary CLR objects: LINQ-to-XML nodes stay nodes, atomic values
    /// become their typed values.
    /// </summary>
    private static List<object?> Drain(EngineIterator iterator)
    {
        var items = new List<object?>();
        foreach (XPathItem item in iterator)
        {
            if (item is XPathNavigator navigator && navigator.UnderlyingObject is XObject node)
                items.Add(node);
            else if (item.IsNode)
                items.Add(item.Value);
            else
                items.Add(item.TypedValue);
        }

        return items;
    }

    private static string Text(object? item, bool trim)
    {
        var text = item switch
        {
            null => string.Empty,
            XElement e => e.Value,
            XAttribute a => a.Value,
            XText t => t.Value,
            XComment c => c.Value,
            XProcessingInstruction pi => pi.Data,
            XDocument d => d.Root?.Value ?? string.Empty,
            // The engine can hand back a bare navigator (result type Navigator) rather than an
            // iterator; its Value is the node's string value, which is what text means here.
            XPathItem xpathItem => xpathItem.Value,
            string s => s,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => item.ToString() ?? string.Empty
        };

        return trim ? text.Trim() : text;
    }

    // ── Conversions ──

    private static T ConvertScalar<T>(object? value, string path, bool trim)
    {
        if (value is T direct)
            return direct;

        if (typeof(T) == typeof(string))
            return (T)(object)Text(value, trim);

        if (typeof(T) == typeof(object))
            return (T)(object)(value is string s ? (trim ? s.Trim() : s) : value)!;

        return Parse<T>(Text(value, trim), path);
    }

    private static T ConvertSingle<T>(object? item, string path, bool trim)
    {
        if (typeof(T) == typeof(object))
            return (T)(object)(item is XObject ? SmartParse(Text(item, trim)) : item)!;

        if (item is T direct)
            return direct;

        if (typeof(T) == typeof(string))
            return (T)(object)Text(item, trim);

        return Parse<T>(Text(item, trim), path);
    }

    private static T ConvertMany<T>(List<object?> items, string path, bool trim)
    {
        var texts = items.Select(item => Text(item, trim)).ToArray();

        if (typeof(T) == typeof(string))
            return (T)(object)string.Join(", ", texts);

        if (typeof(T) == typeof(string[]) || typeof(T) == typeof(object))
            return (T)(object)texts;

        if (typeof(T) == typeof(int[]))
            return (T)(object)texts.Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();

        if (typeof(T) == typeof(double[]))
            return (T)(object)texts.Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

        if (typeof(T) == typeof(XElement[]))
            return (T)(object)items.OfType<XElement>().ToArray();

        if (typeof(T) == typeof(List<XElement>))
            return (T)(object)items.OfType<XElement>().ToList();

        if (typeof(T).IsArray)
        {
            var elementType = typeof(T).GetElementType()!;
            var array = Array.CreateInstance(elementType, texts.Length);
            for (var i = 0; i < texts.Length; i++)
                array.SetValue(System.Convert.ChangeType(texts[i], elementType, CultureInfo.InvariantCulture), i);
            return (T)(object)array;
        }

        throw new InvalidOperationException(
            $"Cannot convert {items.Count} results of XPath 2.0 '{path}' to {typeof(T).Name}.");
    }

    private static object SmartParse(string text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;
        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d))
            return d;
        if (bool.TryParse(text, out var b))
            return b;
        return text;
    }

    private static T Parse<T>(string text, string path)
    {
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (target == typeof(bool))
        {
            if (bool.TryParse(text, out var b)) return (T)(object)b;
            if (text == "1") return (T)(object)true;
            if (text == "0") return (T)(object)false;
        }

        try
        {
            return (T)System.Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Cannot convert the result '{text}' of XPath 2.0 '{path}' to {typeof(T).Name}.", ex);
        }
    }
}
