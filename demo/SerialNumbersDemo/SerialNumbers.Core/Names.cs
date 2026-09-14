using redb.Route.Abstractions;
using SerialNumbers.Domain.Entities;

namespace SerialNumbers.Core;

/// <summary>Exchange headers the routes of this module set and read.</summary>
public static class SerialHeaders
{
    public const string Partner = "serials.partner";
    public const string Transport = "serials.transport";
    public const string FileName = "serials.fileName";

    /// <summary>Identity of the delivered file, the key of the idempotent consumer.</summary>
    public const string MessageKey = "serials.messageKey";

    public const string ArchivePath = "serials.archivePath";

    /// <summary>Root element of the XML; absent when the file is not well-formed XML.</summary>
    public const string MessageType = "serials.messageType";

    public const string RequestId = "serials.requestId";

    /// <summary>Accepted or Rejected, set by the request registration.</summary>
    public const string Decision = "serials.decision";

    /// <summary>File name of the quota report, stamped with the time the schedule fired.</summary>
    public const string ReportFileName = "serials.reportFileName";
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

    public static string Delivery(string partnerCode) => $"direct:deliver-{partnerCode}";

    /// <summary><see cref="Delivery"/> as a template resolved per exchange from the outbox row.</summary>
    public const string DeliveryForOutboxRow = "direct:deliver-${header.partner_code}";
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

    /// <summary>The idempotent repository that remembers every delivered file.</summary>
    public const string InboundFiles = "inbound-files";
}
