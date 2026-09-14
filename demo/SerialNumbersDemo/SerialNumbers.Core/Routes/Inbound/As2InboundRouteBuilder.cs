using redb.Route.As2;
using redb.Route.As2.Fluent;
using redb.Route.Core;
using SerialNumbers.Core.Services;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core.Routes.Inbound;

/// <summary>
/// One AS2 receive endpoint per AS2 partner, all on one port and told apart by the path. The
/// connector decrypts and verifies the message against the partner's certificates and answers
/// with a signed MDN: positive when the route completes, negative when it fails, so a partner
/// whose message could not be stored knows to send it again.
/// </summary>
public sealed class As2InboundRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        var settings = ModuleSettings.FromContext(Context!);

        foreach (var partner in ContextProperties.PartnersOf(Context!).Where(p => p.Transport == Transports.As2))
        {
            var code = partner.Code;

            From(As2.Receive($"/as2/{code}")
                    .Host("0.0.0.0")
                    .Port(settings.As2ReceivePort)
                    .ConnectionFactory(code))       // certificates and the agreed profile live in the registry
                .RouteId($"as2-inbound-{code}")
                .SetHeader(SerialHeaders.Partner, code)
                .SetHeader(SerialHeaders.Transport, Transports.As2)
                .SetHeader(SerialHeaders.FileName, e => ArchivePaths.FromAs2MessageId(e.In.GetHeader<string>(As2Headers.MessageId)))
                .SetHeader(SerialHeaders.MessageKey, e => $"{code}/{e.In.Headers[As2Headers.MessageId]}")
                .To(RouteUris.Intake);
        }
    }
}
