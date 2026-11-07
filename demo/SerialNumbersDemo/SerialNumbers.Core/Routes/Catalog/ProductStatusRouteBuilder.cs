using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http.Rest;
using redb.Route.RedbCore.Extensions;
using SerialNumbers.Core.Catalog;

namespace SerialNumbers.Core.Routes.Catalog;

/// <summary>
/// Changing a product's status, from a web page or from code in the same process.
/// <list type="bullet">
///   <item>A web UI calls <c>PUT /api/products/{gtin}/status</c> with <c>{"status":"Active","changedBy":"alice"}</c>.
///     The REST DSL turns the path parameter into the <c>gtin</c> header, binds the JSON body to
///     <see cref="ProductStatusChange"/> and serves an OpenAPI document at <c>/api/products/openapi.json</c>.</item>
///   <item>Code in the same process sends the same message to <c>direct:set-product-status</c> with a
///     <c>ProducerTemplate</c>, as the debug host's console does.</item>
/// </list>
/// The redb product and the row of the EF Core status journal are written in one transaction. Only after
/// it commits are the requests on hold released, each in a transaction of its own.
/// </summary>
public sealed class ProductStatusRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        var settings = ModuleSettings.FromContext(Context!);

        this.Rest("/api/products", o => { o.Port = settings.ApiPort; o.BindingMode = RestBindingMode.Json; })
            .Put("/{gtin}/status").Consumes("application/json").Type<ProductStatusChange>()
            .To(RouteUris.SetProductStatus);

        From(RouteUris.SetProductStatus)
            .RouteId("set-product-status")
            .Transacted()
                .ProcessWithRedb(ProductCatalog.ChangeStatusAsync)
            .EndTransaction()
            .Choice()
                .When(e => e.In.GetHeader<string>(SerialHeaders.ProductChange) == ProductChangeOutcomes.Refused)
                    .Log("Product status change for ${header.gtin} refused: ${body}", LogLevel.Warning)
                .Otherwise()
                    .Log("${header.gtin}: product status ${header.serials.productStatus} (${header.serials.productChange})")
                    .To(RouteUris.ReleaseHeldRequests)
                    .Process(e => ProductCatalog.Respond(e))
            .EndChoice();
    }
}
