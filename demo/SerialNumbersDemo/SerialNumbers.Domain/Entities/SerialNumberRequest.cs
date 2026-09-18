using redb.Core.Attributes;
using redb.Core.Models.Entities;

namespace SerialNumbers.Domain.Entities;

/// <summary>A partner's request for serial numbers, with the decision taken on it.</summary>
[RedbScheme(Name = "SerialNumbers.Request")]
public sealed class SerialNumberRequest
{
    /// <summary>The partner's own identifier of the request.</summary>
    public string RequestId { get; set; } = "";

    public string PartnerCode { get; set; } = "";

    public string Gtin { get; set; } = "";

    public int Quantity { get; set; }

    /// <summary>
    /// <see cref="Services.MessageStatuses.Accepted"/>, <see cref="Services.MessageStatuses.Rejected"/> or
    /// <see cref="Services.MessageStatuses.OnHold"/>. A request on hold is decided again when its product is activated.
    /// </summary>
    public string Status { get; set; } = "";

    /// <summary>One of <see cref="Services.RejectionReasons"/> when rejected.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>The inbound message this request came in.</summary>
    public RedbObject<InboundMessage>? Message { get; set; }
}
