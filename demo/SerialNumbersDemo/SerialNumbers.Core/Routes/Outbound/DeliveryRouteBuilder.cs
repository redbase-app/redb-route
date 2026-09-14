using redb.Route.As2.Fluent;
using redb.Route.Core;
using redb.Route.Sftp;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core.Routes.Outbound;

/// <summary>
/// One delivery endpoint per partner, over the transport the partner agreed on. The outbox route
/// does not know how a partner is reached; it calls <c>direct:deliver-{partner}</c>.
/// </summary>
public sealed class DeliveryRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        foreach (var partner in ContextProperties.PartnersOf(Context!))
        {
            var route = From(RouteUris.Delivery(partner.Code)).RouteId($"deliver-{partner.Code}");

            if (partner.Transport == Transports.As2)
            {
                // Signed, encrypted, and the partner's signed MDN must confirm it (RequireValidMdn).
                route.To(As2.Send(partner.As2Url!).ConnectionFactory(partner.Code));
            }
            else
            {
                // Written under a temporary name and renamed when complete, so the partner never
                // picks up a half-written file.
                route.To(Sftp.Directory(partner.SftpOutboundFolder!)
                    .ConnectionFactory(partner.Code)
                    .FileName("${header.serials.fileName}")
                    .TempFileName("${header.serials.fileName}.part"));
            }
        }
    }
}
