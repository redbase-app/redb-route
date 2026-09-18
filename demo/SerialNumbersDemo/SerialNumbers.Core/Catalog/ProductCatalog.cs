using Microsoft.EntityFrameworkCore;
using redb.Core;
using redb.Route.Abstractions;
using redb.Route.Http;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core.Catalog;

/// <summary>
/// A product's status change: the redb object and a row of the EF Core status journal, written in the
/// transaction the route opens with <c>.Transacted()</c>.
/// </summary>
public static class ProductCatalog
{
    public static async Task ChangeStatusAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        // EF Core joins the transaction through the connection redb holds for it. Outside a transaction
        // the two writes would commit separately, so that is refused instead of silently allowed.
        if (System.Transactions.Transaction.Current is null)
            throw new InvalidOperationException("A product status change must run inside .Transacted().");

        var gtin = exchange.In.GetHeader<string>(SerialHeaders.Gtin) ?? "";
        if (exchange.In.Body is not ProductStatusChange change || !ProductStatuses.IsKnown(change.Status))
        {
            Refuse(exchange, 400,
                $"The status must be {ProductStatuses.Draft}, {ProductStatuses.Active} or {ProductStatuses.Obsolete}.");
            return;
        }

        var product = await redb.Query<Product>()
            .WhereRedb(o => o.ValueUnique == gtin)
            .FirstOrDefaultAsync();
        if (product is null)
        {
            Refuse(exchange, 404, $"There is no product with GTIN {gtin}.");
            return;
        }

        await using var journal = CatalogAuditDbContext.On(await redb.Context.Db.GetUnderlyingConnectionAsync(ct));

        if (product.Props.Status == change.Status)
        {
            // Nothing to write. Requests on hold are still on hold, so calling the API again retries a
            // release that failed.
            exchange.In.Headers[SerialHeaders.ProductChange] = ProductChangeOutcomes.Unchanged;
        }
        else
        {
            var record = new ProductStatusChangeRecord
            {
                Gtin = gtin,
                FromStatus = product.Props.Status,
                ToStatus = change.Status,
                ChangedBy = string.IsNullOrWhiteSpace(change.ChangedBy) ? "api" : change.ChangedBy,
                ChangedAt = DateTime.UtcNow,
            };

            product.Props.Status = change.Status;
            await redb.SaveAsync(product, ct);

            journal.StatusChanges.Add(record);
            await journal.SaveChangesAsync(ct);

            exchange.In.Headers[SerialHeaders.ProductChange] = ProductChangeOutcomes.Changed;
        }

        exchange.In.Headers[SerialHeaders.ProductStatus] = change.Status;
    }

    /// <summary>The answer of the API, once the requests on hold have been released.</summary>
    public static void Respond(IExchange exchange) =>
        exchange.In.Body = new ProductStatusResult(
            exchange.In.GetHeader<string>(SerialHeaders.Gtin)!,
            exchange.In.GetHeader<string>(SerialHeaders.ProductStatus)!,
            exchange.In.GetHeader<string>(SerialHeaders.ProductChange)!,
            exchange.In.GetHeader<int>(SerialHeaders.HeldCount));

    private static void Refuse(IExchange exchange, int statusCode, string error)
    {
        exchange.In.Headers[SerialHeaders.ProductChange] = ProductChangeOutcomes.Refused;
        exchange.In.Headers[HttpHeaders.ResponseCode] = statusCode;
        exchange.In.Body = new ApiError(error);
    }
}
