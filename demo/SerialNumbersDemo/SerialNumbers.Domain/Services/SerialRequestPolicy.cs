using SerialNumbers.Domain.Entities;

namespace SerialNumbers.Domain.Services;

/// <summary>
/// The business decision on a serial number request, as a pure function: no database, no route.
/// The caller gathers the facts (the product, what was already allocated this year, whether the
/// request id was seen before) and gets back either <c>null</c> (accept) or a rejection reason.
/// </summary>
public static class SerialRequestPolicy
{
    public static string? Evaluate(Product? product, int quantity, long allocatedThisYear, bool isDuplicate)
    {
        if (isDuplicate)
            return RejectionReasons.DuplicateRequest;

        if (product is null)
            return RejectionReasons.UnknownProduct;

        if (quantity <= 0 || quantity > product.MaxPerRequest)
            return RejectionReasons.QuantityOutOfRange;

        if (allocatedThisYear + quantity > product.AnnualQuota)
            return RejectionReasons.AnnualQuotaExhausted;

        return null;
    }
}
