namespace SerialNumbers.Domain.Services;

/// <summary>Why a serial number request was rejected. Sent back to the partner as is.</summary>
public static class RejectionReasons
{
    public const string UnknownProduct = "UnknownProduct";
    public const string QuantityOutOfRange = "QuantityOutOfRange";
    public const string AnnualQuotaExhausted = "AnnualQuotaExhausted";
    public const string DuplicateRequest = "DuplicateRequest";
}
