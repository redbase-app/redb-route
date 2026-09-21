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

            // One consumer per partner, because a partner is a separate host and account. Several
            // directories of ONE partner are a different matter: do not add a route per directory, since
            // each one holds its own connection and partners often allow a single concurrent session.
            // Poll their common parent instead and let the path filters pick the directories out:
            //   .Recursive().AntInclude("TYPE_A/outbox/*.xml,TYPE_B/outbox/*.xml")
            // FilterDirectory decides per directory before it is listed, so the ones left out cost nothing.
            From(Sftp.Directory(partner.SftpInboundFolder!)
                    .ConnectionFactory(code)        // host and credentials live in the registry, not here
                    .Include("*.xml")
                    .Delay(2000)
                    .MoveTo(".done")                // after success only; a failed file stays for the next poll
                    // Poll less often while failures repeat: after three failed polls in a row the next ten
                    // are skipped. A poll fails when the server cannot be reached and, with
                    // BackoffOnFailedExchanges, also when every file it picked up failed, as it does
                    // while the database is down.
                    .BackoffErrorThreshold(3)
                    .BackoffMultiplier(10)
                    .BackoffOnFailedExchanges())
                .RouteId($"sftp-inbound-{code}")
                .SetHeader(SerialHeaders.Partner, code)
                .SetHeader(SerialHeaders.Transport, Transports.Sftp)
                .SetHeader(SerialHeaders.FileName, Header(SftpHeaders.FileName))
                .Log()
                    .Message("${header.serials.partner}: received ${header.serials.fileName} over SFTP")
                    .Header(SftpHeaders.FileLength)
                .EndLog()
                .To(RouteUris.Intake);
        }
    }
}
