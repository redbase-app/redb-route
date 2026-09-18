using redb.Route.Abstractions;
using SerialNumbers.Domain.Entities;

namespace SerialNumbers.Core;

/// <summary>Exchange headers the routes of this module set and read.</summary>
public static class SerialHeaders
{
    public const string Partner = "serials.partner";
    public const string Transport = "serials.transport";
    public const string FileName = "serials.fileName";

    public const string ArchivePath = "serials.archivePath";

    /// <summary>Root element of the XML; absent when the file is not well-formed XML.</summary>
    public const string MessageType = "serials.messageType";

    public const string RequestId = "serials.requestId";

    /// <summary>
    /// Set by the request registration: Accepted, Rejected or OnHold, or
    /// <see cref="ReleaseOutcomes.AlreadyDecided"/> when a release finds the request decided already.
    /// </summary>
    public const string Decision = "serials.decision";

    /// <summary>
    /// The object id of a request on hold whose archived file is sent through the request route again.
    /// Absent for a file a partner delivered.
    /// </summary>
    public const string ReleaseOf = "serials.releaseOf";

    /// <summary>File name of the quota report, stamped with the time the schedule fired.</summary>
    public const string ReportFileName = "serials.reportFileName";

    /// <summary>The GTIN in the path of the product API, <c>/api/products/{gtin}/status</c>.</summary>
    public const string Gtin = "gtin";

    /// <summary>The status a product has after the status change.</summary>
    public const string ProductStatus = "serials.productStatus";

    /// <summary>One of <see cref="ProductChangeOutcomes"/>.</summary>
    public const string ProductChange = "serials.productChange";

    /// <summary>How many requests on hold a product activation found.</summary>
    public const string HeldCount = "serials.heldCount";
}

/// <summary>Exchange properties that carry redb objects between the steps of one transaction.</summary>
public static class SerialProperties
{
    public const string Request = "serials.request";
    public const string Response = "serials.response";
}

/// <summary>Internal endpoints that join the routes of the module.</summary>
public static class RouteUris
{
    public const string Intake = "direct:intake";
    public const string SerialNumberRequest = "direct:serial-number-request";

    /// <summary>Changes a product's status: called by the REST API and by the debug host's console.</summary>
    public const string SetProductStatus = "direct:set-product-status";

    public const string ReleaseHeldRequests = "direct:release-held-requests";

    public static string Delivery(string partnerCode) => $"direct:deliver-{partnerCode}";

    /// <summary><see cref="Delivery"/> as a template resolved per exchange from the outbox row.</summary>
    public const string DeliveryForOutboxRow = "direct:deliver-${header.partner_code}";
}

/// <summary>What a status change did.</summary>
public static class ProductChangeOutcomes
{
    public const string Changed = "Changed";
    public const string Unchanged = "Unchanged";

    /// <summary>Unknown product or status: answered with an error, nothing written.</summary>
    public const string Refused = "Refused";
}

/// <summary>Outcomes of the request route that are not a decision on the request.</summary>
public static class ReleaseOutcomes
{
    /// <summary>A release found the request no longer on hold: a concurrent release got there first.</summary>
    public const string AlreadyDecided = "AlreadyDecided";
}

/// <summary>
/// Properties the module adds to its route context, next to the configuration Tsak put there. Route
/// builders read both in <c>Configure()</c> through <c>Context</c>.
/// </summary>
public static class ContextProperties
{
    /// <summary>The partners read from redb when the module loads; inbound and delivery routes are built per partner.</summary>
    public const string Partners = "serials.partners";

    public static IReadOnlyList<Partner> PartnersOf(IRouteContext context) =>
        context.GetProperty<IReadOnlyList<Partner>>(Partners)
        ?? throw new InvalidOperationException("The partner list is not in the route context; InitRoute.main puts it there before the routes are built.");
}

/// <summary>Names of the objects registered in the route context registry.</summary>
public static class RegistryNames
{
    /// <summary>The <c>sql:</c> data source: the same SQL Server database redb lives in.</summary>
    public const string SerialsDatabase = "serials";
}
