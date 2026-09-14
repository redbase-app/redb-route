using redb.Core.Attributes;

namespace SerialNumbers.Domain.Entities;

/// <summary>
/// Every file a partner sent, whatever became of it. The raw bytes live in the file archive
/// (<see cref="ArchivePath"/>); this object is the record of what the hub made of them.
/// Nothing is ever deleted.
/// </summary>
[RedbScheme(Name = "SerialNumbers.InboundMessage")]
public sealed class InboundMessage
{
    public string PartnerCode { get; set; } = "";

    public string Transport { get; set; } = "";

    public string FileName { get; set; } = "";

    /// <summary>The root element of the XML, when it could be read.</summary>
    public string? MessageType { get; set; }

    /// <summary>Path of the raw copy, relative to the archive root.</summary>
    public string ArchivePath { get; set; } = "";

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>One of <see cref="Services.MessageStatuses"/>.</summary>
    public string Status { get; set; } = "";

    /// <summary>Why the message was not processed, for Invalid and Parked messages.</summary>
    public string? Error { get; set; }
}
