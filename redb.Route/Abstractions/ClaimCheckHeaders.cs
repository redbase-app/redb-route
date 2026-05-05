namespace redb.Route.Abstractions;

/// <summary>
/// Well-known header and property constants used by the Claim Check pattern.
/// </summary>
public static class ClaimCheckHeaders
{
    /// <summary>Claim key stored/read by ClaimCheck processors.</summary>
    public const string Key = "ClaimCheck.Key";

    /// <summary>Original content type before claim check storage.</summary>
    public const string OriginalContentType = "ClaimCheck.OriginalContentType";

    /// <summary>Original CLR body type for typed deserialization.</summary>
    public const string OriginalBodyType = "ClaimCheck.OriginalBodyType";

    /// <summary>Exchange property key for the push/pop key stack.</summary>
    internal const string StackPropertyKey = "ClaimCheck.Stack";
}
