using redb.Route.Core;
using redb.Route.RedbCore.Extensions;
using redb.Route.Sql;
using SerialNumbers.Core.Services;

namespace SerialNumbers.Core.Routes.Outbound;

/// <summary>
/// Delivers queued responses. The <c>sql:</c> consumer polls the outbox; every row is one exchange.
/// <para>
/// A delivered row is marked sent; a failed one counts the attempt and keeps the error, and is tried
/// again on the next poll, up to ten times. The row is marked after the delivery, so a crash between
/// the two sends the file once more: at-least-once, the partner recognises a repeat by the file name.
/// </para>
/// </summary>
public sealed class OutboxRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        From(Sql.Poll("SELECT TOP (20) id, partner_code, response_object_id FROM dbo.outbox " +
                      "WHERE sent_at IS NULL AND attempts < 10 ORDER BY id")
                .DataSource(Constant(RegistryNames.SerialsDatabase))
                .Delay(2000)
                .OnSuccess("UPDATE dbo.outbox SET sent_at = SYSUTCDATETIME(), attempts = attempts + 1, last_error = NULL WHERE id = :#id")
                .OnFailure("UPDATE dbo.outbox SET attempts = attempts + 1, last_error = LEFT(:#redbError, 2000) WHERE id = :#id"))
            .RouteId("outbox")
            .ProcessWithRedb(ResponseRenderer.RenderAsync)
            .Marshal("application/xml")
            .ToD(RouteUris.DeliveryForOutboxRow)
            .Log("${header.partner_code}: delivered ${header.serials.fileName}");
    }
}
