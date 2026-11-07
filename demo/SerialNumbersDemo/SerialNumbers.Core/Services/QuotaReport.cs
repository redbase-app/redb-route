using System.Globalization;
using System.Text;
using redb.Core;
using redb.Route.Abstractions;
using SerialNumbers.Domain.Entities;

namespace SerialNumbers.Core.Services;

/// <summary>
/// The quota usage report: what the allocation ledger says was issued this year, set against the
/// annual quota of each product. The numbers come from a flat table, the quotas from redb objects.
/// </summary>
public static class QuotaReport
{
    public const string CsvHeader = "gtin,product,requests,issued,annual_quota,remaining,used_percent";

    public static async Task RenderCsvAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        // The sql: producer leaves the result set in the body, one dictionary per row.
        var rows = (List<Dictionary<string, object?>>)exchange.In.Body!;
        var gtins = rows.Select(r => (string)r["gtin"]!).ToList();

        // One query for every product in the report, not one per row.
        var products = await redb.Query<Product>()
            .Where(p => gtins.Contains(p.Gtin))
            .ToListAsync();
        var byGtin = products.ToDictionary(o => o.Props.Gtin, o => o.Props);

        var csv = new StringBuilder().AppendLine(CsvHeader);
        foreach (var row in rows)
        {
            var gtin = (string)row["gtin"]!;
            var requests = Convert.ToInt64(row["requests"], CultureInfo.InvariantCulture);
            var issued = Convert.ToInt64(row["issued"], CultureInfo.InvariantCulture);

            csv.Append(gtin).Append(',');
            if (byGtin.TryGetValue(gtin, out var product))
            {
                var remaining = product.AnnualQuota - issued;
                var usedPercent = product.AnnualQuota == 0 ? 0m : 100m * issued / product.AnnualQuota;
                csv.Append(Quote(product.Name)).Append(',')
                    .Append(requests).Append(',')
                    .Append(issued).Append(',')
                    .Append(product.AnnualQuota).Append(',')
                    .Append(remaining).Append(',')
                    .Append(usedPercent.ToString("0.0", CultureInfo.InvariantCulture));
            }
            else
            {
                // Allocated once, no longer in the catalog: the numbers stay, the quota columns are empty.
                csv.Append(",").Append(requests).Append(',').Append(issued).Append(",,,");
            }
            csv.AppendLine();
        }

        exchange.In.Body = csv.ToString();
        exchange.In.ContentType = "text/csv";
    }

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}
