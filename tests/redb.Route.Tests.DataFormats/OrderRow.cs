namespace redb.Route.Tests.DataFormats;

/// <summary>POCO shared by the CSV / Avro / YAML round-trip tests.</summary>
public sealed class OrderRow
{
    public int Id { get; set; }
    public string Customer { get; set; } = "";
    public decimal Amount { get; set; }

    public override bool Equals(object? obj) => obj is OrderRow o && o.Id == Id && o.Customer == Customer && o.Amount == Amount;
    public override int GetHashCode() => HashCode.Combine(Id, Customer, Amount);
    public override string ToString() => $"{Id}/{Customer}/{Amount}";
}
