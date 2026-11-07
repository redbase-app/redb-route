namespace redb.Route.Http.Rest;

/// <summary>Where a declared REST parameter comes from.</summary>
public enum RestParamType
{
    /// <summary>A <c>{name}</c> segment of the path template. Always required.</summary>
    Path,

    /// <summary>A query string parameter.</summary>
    Query,

    /// <summary>A request header.</summary>
    Header,
}

/// <summary>The value type of a declared REST parameter, as OpenAPI names it.</summary>
public enum RestParamDataType
{
    /// <summary>Any text.</summary>
    String,

    /// <summary>A whole number (invariant culture, optional sign).</summary>
    Integer,

    /// <summary>A decimal number (invariant culture).</summary>
    Number,

    /// <summary><c>true</c> or <c>false</c>.</summary>
    Boolean,
}

/// <summary>
/// One parameter declared on a REST verb with <c>Param(...)</c>: described in the OpenAPI document
/// always, enforced only when client request validation is on.
/// </summary>
/// <param name="Name">Parameter name: the template segment, the query key or the header name.</param>
/// <param name="Type">Where the parameter comes from.</param>
/// <param name="Required">A missing required parameter is answered with 400.</param>
/// <param name="DataType">A value that does not convert to this type is answered with 400.</param>
/// <param name="Description">OpenAPI <c>description</c>.</param>
public sealed record RestParamDefinition(
    string Name,
    RestParamType Type,
    bool Required,
    RestParamDataType DataType,
    string? Description);
