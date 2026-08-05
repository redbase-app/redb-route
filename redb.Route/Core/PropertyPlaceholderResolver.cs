using System.Text.RegularExpressions;

namespace redb.Route.Core;

/// <summary>
/// Resolves Apache Camel-style property placeholders inside a string — typically an endpoint URI —
/// at route-compile time. Supports <c>{{key}}</c> (required) and <c>{{key:default}}</c> (with a fallback
/// value). This is the redb.Route analogue of Camel's <c>PropertiesComponent</c>: it lets endpoint URIs
/// (and any other DSL string routed through it) externalise their configuration instead of hard-coding
/// hosts, queue names, ports or paths.
/// <para>
/// The value lookup is supplied by the caller as a <see cref="Func{T,TResult}"/>, keeping this type pure
/// and testable. <see cref="RouteContext"/> wires the lookup to <c>IConfiguration</c> (which already layers
/// environment variables, appsettings and user-secrets with the container's own precedence — so no
/// Camel-style <c>env:</c>/<c>sys:</c> prefix functions are needed) and, as a container-free fallback,
/// the context's own properties.
/// </para>
/// <para>
/// Deliberately minimal: no nested placeholders (<c>{{a{{b}}}}</c>), no optional-parameter removal
/// (<c>{{?key}}</c>), no encryption or multi-location precedence. Resolution is single-pass, so it can
/// never loop. A placeholder with neither a value nor a default fails fast at compile time.
/// </para>
/// </summary>
public static class PropertyPlaceholderResolver
{
    // {{ key }} or {{ key : default }}. The key excludes braces and the ':' separator; the optional
    // default (everything up to the closing braces) may contain ':' so URLs work as defaults. Lazy so
    // the first "}}" terminates the match — a nested inner placeholder simply isn't matched as one.
    private static readonly Regex Pattern = new(
        @"\{\{\s*([^{}:]+?)\s*(?::\s*([^{}]*?)\s*)?\}\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Fast pre-check: <c>true</c> only if <paramref name="text"/> could contain a placeholder. Lets callers
    /// skip resolution (and the regex) for the overwhelming majority of URIs that have none — which is also
    /// why the feature never touches existing routes: <c>{{</c> is not legal in a bare URI.
    /// </summary>
    public static bool HasPlaceholder(string? text)
        => text is not null && text.Contains("{{", StringComparison.Ordinal);

    /// <summary>
    /// Replaces every <c>{{key}}</c> / <c>{{key:default}}</c> in <paramref name="text"/> with the value
    /// returned by <paramref name="lookup"/> for that key (or the default when the lookup yields none).
    /// </summary>
    /// <param name="text">The string to resolve (e.g. an endpoint URI).</param>
    /// <param name="lookup">Returns the configured value for a key, or <c>null</c>/empty if unknown.</param>
    /// <returns>The resolved string, or <paramref name="text"/> unchanged when it has no placeholders.</returns>
    /// <exception cref="InvalidOperationException">
    /// A placeholder had no resolvable value and no default — surfaced at compile time so a misconfigured
    /// deployment fails loudly rather than building a broken endpoint URI.
    /// </exception>
    public static string Resolve(string text, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (!HasPlaceholder(text))
            return text;

        return Pattern.Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            var value = lookup(key);
            if (!string.IsNullOrEmpty(value))
                return value;

            if (match.Groups[2].Success)
                return match.Groups[2].Value;

            throw new InvalidOperationException(
                $"Unresolved property placeholder '{{{{{key}}}}}': no value was found for key '{key}' " +
                $"and no default was given (use '{{{{{key}:default}}}}'). Provide the value via IConfiguration " +
                $"(appsettings/environment) or context.SetProperty(\"{key}\", ...).");
        });
    }
}
