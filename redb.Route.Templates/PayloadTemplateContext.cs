using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Xml;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;

namespace redb.Route.Templates;

/// <summary>
/// A value the template author has marked as already safe for the result document
/// (<c>{{ fragment | raw }}</c>); written verbatim, bypassing media-type escaping.
/// </summary>
public sealed class RawString
{
    /// <summary>Wraps text that must not be escaped.</summary>
    public RawString(string? value) => Value = value ?? string.Empty;

    /// <summary>The text.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// How a value becomes text — shared by the Scriban and the Liquid context. Two steps, deliberately
/// apart: <see cref="ToText"/> is the culture-invariant conversion (dates as ISO 8601) Scriban applies
/// wherever a value turns into a string inside the template (concatenation, filters), and
/// <see cref="Format"/> adds the escaping for the <see cref="MediaType"/> of the result, applied
/// exactly once, at the point a value is written to the output. Escaping inside <c>ObjectToString</c>
/// instead would escape a value again for every operator it passed through. Template literals never
/// come this way; <see cref="RawString"/> is written verbatim.
/// </summary>
internal static class PayloadValueFormatter
{
    /// <summary>Value → text, culture-invariant, no escaping.</summary>
    public static string ToText(object? value, Func<string> fallback) => value switch
    {
        null => string.Empty,
        RawString raw => raw.Value,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => fallback(),
    };

    /// <summary>Value → output text: <see cref="ToText"/>, then escaped once for the media type; a <see cref="RawString"/> verbatim.</summary>
    public static string Format(object? value, MediaType mediaType, Func<string> fallback)
        => value is RawString raw ? raw.Value : Escape(ToText(value, fallback), mediaType);

    public static void Configure(TemplateContext context, RouteTemplateOptions options)
    {
        context.MemberRenamer = member => member.Name;   // body.OrderId, not body.order_id
        context.StrictVariables = options.StrictVariables;
        context.EnableRelaxedMemberAccess = !options.StrictVariables;
        context.LoopLimit = options.LoopLimit;
        context.TemplateLoader = null;                    // no include from arbitrary paths (README decision 4)
    }

    private static string Escape(string text, MediaType mediaType) => mediaType switch
    {
        MediaType.Json => JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(text),
        MediaType.Xml => XmlEscape(text),
        _ => text,
    };

    /// <summary>
    /// The five XML entities, plus a replacement character for what XML 1.0 cannot carry at all:
    /// control characters other than tab / LF / CR and lone surrogates. Left in, they would make the
    /// document unparseable; U+FFFD is the same choice the JSON encoder makes for a lone surrogate.
    /// </summary>
    private static string XmlEscape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        sb.Append(c).Append(text[++i]);
                    else if (char.IsSurrogate(c) || !XmlConvert.IsXmlChar(c))
                        sb.Append('�');
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>Scriban-syntax render context with media-type escaping (see <see cref="PayloadValueFormatter"/>).</summary>
internal sealed class PayloadTemplateContext : TemplateContext
{
    private readonly MediaType _mediaType;

    public PayloadTemplateContext(MediaType mediaType, RouteTemplateOptions options)
    {
        _mediaType = mediaType;
        PayloadValueFormatter.Configure(this, options);
    }

    /// <summary>Invariant text for every in-template conversion (concatenation, filters); no escaping here.</summary>
    public override string ObjectToString(object? value, bool nested = false)
        => PayloadValueFormatter.ToText(value, () => base.ObjectToString(value, nested) ?? string.Empty);

    /// <summary>The one place a value meets the result document: escaped here, once, whatever operators or filters produced it.</summary>
    public override TemplateContext Write(SourceSpan span, object? textAsObject)
    {
        if (textAsObject is not null)
            Write(PayloadValueFormatter.Format(textAsObject, _mediaType, () => ObjectToString(textAsObject)));
        return this;
    }
}

/// <summary>Liquid-syntax render context (Liquid filters such as <c>upcase</c> registered) with the same escaping.</summary>
internal sealed class LiquidPayloadTemplateContext : LiquidTemplateContext
{
    private readonly MediaType _mediaType;

    public LiquidPayloadTemplateContext(MediaType mediaType, RouteTemplateOptions options)
    {
        _mediaType = mediaType;
        PayloadValueFormatter.Configure(this, options);
    }

    /// <summary>Invariant text for every in-template conversion (concatenation, filters); no escaping here.</summary>
    public override string ObjectToString(object? value, bool nested = false)
        => PayloadValueFormatter.ToText(value, () => base.ObjectToString(value, nested) ?? string.Empty);

    /// <summary>The one place a value meets the result document: escaped here, once, whatever operators or filters produced it.</summary>
    public override TemplateContext Write(SourceSpan span, object? textAsObject)
    {
        if (textAsObject is not null)
            Write(PayloadValueFormatter.Format(textAsObject, _mediaType, () => ObjectToString(textAsObject)));
        return this;
    }
}
