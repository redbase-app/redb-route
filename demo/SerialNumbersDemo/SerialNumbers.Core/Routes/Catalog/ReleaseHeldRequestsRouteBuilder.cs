using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;
using SerialNumbers.Core.Services;

namespace SerialNumbers.Core.Routes.Catalog;

/// <summary>
/// Releases the requests that waited for a product to be activated. The partner sends nothing again:
/// each request's file is read back from the archive and goes through the request route, where the
/// request is decided against the product and the quota as they are now.
/// <para>
/// One request at a time, each in the request route's own transaction. A request that was released is
/// no longer on hold, so a second activation call finds nothing to release. A failure stops the batch
/// and reaches the caller; the requests not released stay on hold, and calling the API again releases
/// them.
/// </para>
/// </summary>
public sealed class ReleaseHeldRequestsRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        var settings = ModuleSettings.FromContext(Context!);

        From(RouteUris.ReleaseHeldRequests)
            .RouteId("release-held-requests")
            .ProcessWithRedb(HeldRequests.FindAsync)
            .Log("${header.gtin}: ${header.serials.heldCount} request(s) on hold to release")
            .Split(e => (IEnumerable<object?>)e.In.Body!)
                .Process((e, ct) => HeldRequests.LoadArchivedFileAsync(e, settings.ArchiveDirectory, ct))
                .Log("${header.serials.partner}: releasing request ${header.serials.requestId} from ${header.serials.archivePath}")
                .To(RouteUris.SerialNumberRequest)
            .EndSplit();
    }
}
