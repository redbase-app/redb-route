using redb.Core.Attributes;
using redb.Core.Models.Entities;

namespace SerialNumbers.Domain.Entities;

/// <summary>
/// The answer to a request, as it is sent back. The individual serial numbers are rows of the
/// <c>serial_numbers</c> table (a request can carry tens of thousands of them); an accepted response
/// points at their allocation.
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

    /// <summary>The <c>serial_allocations</c> row the numbers of an accepted response belong to.</summary>
    public long? AllocationId { get; set; }

    public RedbObject<SerialNumberRequest>? Request { get; set; }
}
