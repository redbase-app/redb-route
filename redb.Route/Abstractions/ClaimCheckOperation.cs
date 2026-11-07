namespace redb.Route.Abstractions;

/// <summary>
/// Defines the operation to perform in a Claim Check step.
/// </summary>
public enum ClaimCheckOperation
{
    /// <summary>Store body under a specific key (key-value mode).</summary>
    Set,

    /// <summary>Retrieve body by key, keep data in store.</summary>
    Get,

    /// <summary>Retrieve body by key, remove data from store.</summary>
    GetAndRemove,

    /// <summary>Push body onto an exchange-scoped stack (no key needed).</summary>
    Push,

    /// <summary>Pop body from the exchange-scoped stack (no key needed).</summary>
    Pop
}
