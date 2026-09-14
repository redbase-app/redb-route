using redb.Core.Attributes;

namespace SerialNumbers.Domain.Entities;

/// <summary>
/// A product serial numbers are issued for. The GTIN is also the object's unique key
/// (<c>ValueUnique</c>), which is what the request lookup goes through.
/// </summary>
[RedbScheme(Name = "SerialNumbers.Product")]
public sealed class Product
{
    public string Gtin { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The largest quantity a single request may ask for.</summary>
    public int MaxPerRequest { get; set; }

    /// <summary>How many serial numbers may be issued for this product in one calendar year.</summary>
    public long AnnualQuota { get; set; }
}
