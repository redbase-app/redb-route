namespace redb.Route.Templates;

/// <summary>
/// Result type of a payload template. Decides how substituted values are escaped and which
/// <c>ContentType</c> the produced body gets. Mandatory: without it a value containing a quote or
/// an ampersand silently breaks the document (WSO2 PayloadFactory defaults to XML, which is a trap).
/// </summary>
public enum MediaType
{
    /// <summary>JSON: <c>"</c>, <c>\</c> and control characters in substituted strings are escaped; <c>ContentType</c> = <c>application/json</c>.</summary>
    Json,

    /// <summary>XML: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c>, <c>'</c> in substituted strings are escaped; <c>ContentType</c> = <c>application/xml</c>.</summary>
    Xml,

    /// <summary>Plain text: no escaping; <c>ContentType</c> = <c>text/plain</c>.</summary>
    Text,
}
