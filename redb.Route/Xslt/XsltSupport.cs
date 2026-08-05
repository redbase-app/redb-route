using redb.Route.Abstractions;

namespace redb.Route.Xslt;

/// <summary>Well-known message headers for the XSLT transform (Apache Camel names).</summary>
public static class XsltHeaders
{
    /// <summary>Header carrying a stylesheet <b>file path / URI</b> to use for this message
    /// (honoured only when <c>allowTemplateFromHeader</c> is enabled).</summary>
    public const string ResourceUri = "CamelXsltResourceUri";

    /// <summary>Header carrying an <b>inline stylesheet</b> document to use for this message
    /// (honoured only when <c>allowTemplateFromHeader</c> is enabled).</summary>
    public const string Stylesheet = "CamelXsltStylesheet";
}

/// <summary>
/// Collects XSLT stylesheet parameters from the exchange. Mirrors Apache Camel, which makes all
/// message headers (and exchange variables/properties) available as <c>xsl:param</c> — a stylesheet
/// only sees the ones it declares. Values are passed as strings; the transform silently ignores keys
/// that are not usable parameter names.
/// </summary>
public static class XsltParameters
{
    /// <summary>Builds the parameter map (headers first, then properties without overwriting).</summary>
    public static IReadOnlyDictionary<string, object?> FromExchange(IExchange exchange)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in exchange.In.Headers)
            if (value is not null)
                parameters[key] = value.ToString();

        foreach (var (key, value) in exchange.Properties)
            if (value is not null && !parameters.ContainsKey(key))
                parameters[key] = value.ToString();

        return parameters;
    }
}
