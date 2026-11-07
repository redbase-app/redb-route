using redb.Core;

namespace SerialNumbers.Core.Services;

/// <summary>
/// The outbox: a table row per response to deliver. Written inside the request transaction and
/// read by the outbox route with the <c>sql:</c> component, so a response is either committed and
/// queued, or neither.
/// </summary>
public static class OutboxWriter
{
    public static async Task EnqueueAsync(IRedbService redb, string partnerCode, long responseObjectId, CancellationToken ct) =>
        await redb.Context.ExecuteAsync(
            "INSERT INTO dbo.outbox (partner_code, response_object_id) VALUES ($1, $2)",
            [partnerCode, responseObjectId], ct);
}
