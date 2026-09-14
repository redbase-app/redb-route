using System.Globalization;
using redb.Route.Core;
using redb.Route.Sftp;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core.Routes.Inbound;

/// <summary>
/// One SFTP consumer per partner that exchanges files over SFTP. The route only receives: it names
/// the partner, identifies the file and hands it to the common intake.
/// </summary>
public sealed class SftpInboundRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        foreach (var partner in ContextProperties.PartnersOf(Context!).Where(p => p.Transport == Transports.Sftp))
        {
            var code = partner.Code;

            From(Sftp.Directory(partner.SftpInboundFolder!)
                    .ConnectionFactory(code)        // host and credentials live in the registry, not here
                    .Include("*.xml")
                    .Delay(2000)
                    .MoveTo(".done"))               // after success only; a failed file stays for the next poll
                .RouteId($"sftp-inbound-{code}")
                .SetHeader(SerialHeaders.Partner, code)
                .SetHeader(SerialHeaders.Transport, Transports.Sftp)
                .SetHeader(SerialHeaders.FileName, Header(SftpHeaders.FileName))
                // name + size + modification time: the same file is processed once, a corrected
                // file re-sent under the same name is a new delivery. Invariant formatting: the key is
                // shared by every node, whatever culture each one runs with.
                .SetHeader(SerialHeaders.MessageKey, e => string.Create(CultureInfo.InvariantCulture,
                    $"{code}/{e.In.Headers[SftpHeaders.FileName]}/{e.In.Headers[SftpHeaders.FileLength]}/{e.In.GetHeader<DateTimeOffset>(SftpHeaders.FileLastModified):O}"))
                .To(RouteUris.Intake);
        }
    }
}
