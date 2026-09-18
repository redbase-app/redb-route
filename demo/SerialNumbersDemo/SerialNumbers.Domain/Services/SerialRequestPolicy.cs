using SerialNumbers.Domain.Entities;

namespace SerialNumbers.Domain.Services;

/// <summary>The decision on a request: one of <see cref="MessageStatuses"/>, with the reason of a rejection.</summary>
public sealed record RequestDecision(string Status, string? RejectionReason)
{
    public static readonly RequestDecision Accept = new(MessageStatuses.Accepted, null);
    public static readonly RequestDecision Hold = new(MessageStatuses.OnHold, null);

    public static RequestDecision Reject(string reason) => new(MessageStatuses.Rejected, reason);
}

/// <summary>
/// The business decision on a serial number request, as a pure function: no database, no route.
/// The caller gathers the facts (the product, what was already allocated this year, whether the
/// request id was seen before) and gets back the decision.
/// </summary>
public static class SerialRequestPolicy
{
    public static RequestDecision Decide(Product? product, int quantity, long allocatedThisYear, bool isDuplicate)
    {
        if (isDuplicate)
            return RequestDecision.Reject(RejectionReasons.DuplicateRequest);

        if (product is null)
            return RequestDecision.Reject(RejectionReasons.UnknownProduct);

        switch (product.Status)
        {
            case ProductStatuses.Obsolete:
                return RequestDecision.Reject(RejectionReasons.ProductObsolete);
            case ProductStatuses.Draft:
            case ProductStatuses.Active:
                break;
            default:
                throw new InvalidOperationException(
                    $"Product {product.Gtin} has the status '{product.Status}', which is not one of the known product statuses.");
        }

        if (quantity <= 0 || quantity > product.MaxPerRequest)
            return RequestDecision.Reject(RejectionReasons.QuantityOutOfRange);

        // A draft is our side of the story: the request waits. The quota is checked when the product is
        // activated and the request is decided again, against what has been allocated by then.
        if (product.Status == ProductStatuses.Draft)
            return RequestDecision.Hold;

        if (allocatedThisYear + quantity > product.AnnualQuota)
            return RequestDecision.Reject(RejectionReasons.AnnualQuotaExhausted);

        return RequestDecision.Accept;
    }
}
