using redb.Route.Abstractions;

namespace redb.Route.Configuration;

/// <summary>
/// Strongly-typed view of the <c>redbRoute:Codecs:Json</c> configuration section.
/// Used by <see cref="Extensions.RedbRouteCodecExtensions.ConfigureJsonCodec(Extensions.RedbRouteBuilder, JsonCodecOptions)"/>.
/// <para>
/// These knobs govern the <b>generic</b> JSON codec registered under
/// <c>application/json</c> in <see cref="IDataFormatRegistry"/>. They do NOT override
/// media-type-specific profiles registered separately (e.g. SCIM, Problem Details,
/// OAuth): such profiles own their own options and are not exposed to app config
/// by design.
/// </para>
/// </summary>
public sealed class JsonCodecOptions
{
    /// <summary>
    /// Property naming policy. Accepted values (case-insensitive):
    /// <c>null</c>/<c>"None"</c> (verbatim), <c>"CamelCase"</c>, <c>"SnakeCaseLower"</c>,
    /// <c>"SnakeCaseUpper"</c>, <c>"KebabCaseLower"</c>, <c>"KebabCaseUpper"</c>.
    /// Default: <c>"CamelCase"</c>.
    /// </summary>
    public string? PropertyNamingPolicy { get; set; } = "CamelCase";

    /// <summary>
    /// When <c>true</c>, null property values are omitted during serialization.
    /// Default: <c>true</c>.
    /// </summary>
    public bool IgnoreNullValues { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, property name matching during deserialization is case-insensitive.
    /// Default: <c>true</c>.
    /// </summary>
    public bool PropertyNameCaseInsensitive { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, output JSON is indented. Default: <c>false</c>.
    /// </summary>
    public bool WriteIndented { get; set; }

    /// <summary>
    /// When <c>true</c> (default), uses <c>UnsafeRelaxedJsonEscaping</c> so non-ASCII
    /// bytes (Cyrillic, emoji, etc.) and ASCII quotes are emitted as UTF-8 literals
    /// per RFC 8259 §8.1. Only flip to <c>false</c> if the output is embedded into
    /// HTML/JS inline — which is not a Route-message scenario.
    /// </summary>
    public bool UnsafeRelaxedEscaping { get; set; } = true;
}
