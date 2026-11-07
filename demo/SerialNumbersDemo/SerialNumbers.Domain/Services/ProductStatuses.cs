namespace SerialNumbers.Domain.Services;

/// <summary>Where a product is in its life.</summary>
public static class ProductStatuses
{
    /// <summary>Being set up. Requests for it are put on hold, not rejected: the partner did nothing wrong.</summary>
    public const string Draft = "Draft";

    /// <summary>Serial numbers are issued for it.</summary>
    public const string Active = "Active";

    /// <summary>No longer produced. Requests for it are rejected.</summary>
    public const string Obsolete = "Obsolete";

    public static bool IsKnown(string? status) => status is Draft or Active or Obsolete;
}
