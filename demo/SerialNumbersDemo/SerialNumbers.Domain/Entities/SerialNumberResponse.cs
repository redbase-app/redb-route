using redb.Core.Attributes;
using redb.Core.Models.Entities;

namespace SerialNumbers.Domain.Entities;

/// <summary>
/// The answer to a request, as it is sent back. The individual serial numbers are rows of the
/// <c>serial_numbers</c> table (a request can carry tens of thousands of them); the response
/// holds the range.
/// </summary>
[RedbScheme(Name = "SerialNumbers.Response")]
public sealed class SerialNumberResponse
{
    public string RequestId { get; set; } = "";

    public string PartnerCode { get; set; } = "";

    public string Gtin { get; set; } = "";

    public bool Accepted { get; set; }

    /// <summary>One of <see cref="Services.RejectionReasons"/> when rejected.</summary>
    public string? RejectionReason { get; set; }

    public int Quantity { get; set; }

    public long? FirstSerial { get; set; }

    public long? LastSerial { get; set; }

    public RedbObject<SerialNumberRequest>? Request { get; set; }
}
