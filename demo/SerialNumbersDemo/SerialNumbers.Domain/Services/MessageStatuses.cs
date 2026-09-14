namespace SerialNumbers.Domain.Services;

/// <summary>What happened to an inbound message.</summary>
public static class MessageStatuses
{
    /// <summary>A serial number request was read and answered with numbers.</summary>
    public const string Accepted = "Accepted";

    /// <summary>A serial number request was read and answered with a rejection.</summary>
    public const string Rejected = "Rejected";

    /// <summary>The file is not a valid message: broken XML or a schema violation.</summary>
    public const string Invalid = "Invalid";

    /// <summary>A valid message of a type this hub does not process yet. Kept for later.</summary>
    public const string Parked = "Parked";
}
