using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;
using redb.Route.Validation;
using SerialNumbers.Core.Integration.Xml;
using SerialNumbers.Core.Services;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core.Routes.Processing;

/// <summary>
/// A serial number request from validation to a queued response.
/// <list type="number">
///   <item>Schema and deserialization in <c>DoTry</c>: a schema violation is the partner's error, recorded
///     as Invalid and closed. The file is archived, nothing is retried.</item>
///   <item>One transaction, <c>.Transacted()</c>, for everything the request writes: the message record,
///     the request, the serial numbers, the response and the outbox row. A repeated delivery needs no
///     separate registry of processed files: the request carries the partner's request id as its unique
///     key, so the second one is answered as a duplicate and a delivery that rolled back leaves no trace
///     and is processed again.</item>
///   <item>The business decision is a branch, not an exception: a rejection is a normal answer, and a
///     request for a draft product waits on hold with nothing sent.</item>
/// </list>
/// A technical failure (database, network) is not handled here on purpose: the transaction rolls back,
/// the consumer sees the failure, and the file stays in the partner folder for the next poll.
/// </summary>
public sealed class SerialNumberRequestRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        From(RouteUris.SerialNumberRequest)
            .RouteId("serial-number-request")
            // Every step is timed; when a message fails, the log shows the path it took.
            .MessageHistory()

            .DoTry()
                .ValidateXsd(XmlSchemas.SerialNumberRequest)
                .Unmarshal<SerialNumberRequestXml>("application/xml")
            .DoCatch<ValidationException>()
                .ProcessWithRedb(IntakeRecorder.RecordInvalidAsync)
                .Log("${header.serials.partner}: ${header.serials.fileName} violates the schema, recorded as Invalid", LogLevel.Warning)
                .Stop()
            .EndTryCatch()

            .Transacted()
                .ProcessWithRedb(SerialRequestService.RegisterAsync)
                .Choice()
                    .When(e => DecisionOf(e) == MessageStatuses.Accepted)
                        .ProcessWithRedb(SerialRequestService.AllocateAsync)
                        .ProcessWithRedb(SerialRequestService.QueueResponseAsync)
                    .When(e => DecisionOf(e) == MessageStatuses.Rejected)
                        .ProcessWithRedb(SerialRequestService.RejectAsync)
                        .ProcessWithRedb(SerialRequestService.QueueResponseAsync)
                .EndChoice()
            .EndTransaction()

            // Logged after the commit: the line says what has happened, not what was about to.
            .Choice()
                .When(e => DecisionOf(e) == MessageStatuses.OnHold)
                    .Log("${header.serials.partner}: request ${header.serials.requestId} is on hold until its product is activated")
                .When(e => DecisionOf(e) == ReleaseOutcomes.AlreadyDecided)
                    .Log("${header.serials.partner}: request ${header.serials.requestId} was decided by another release, nothing to do")
                .Otherwise()
                    .Log("${header.serials.partner}: request ${header.serials.requestId} ${header.serials.decision}, response queued")
            .EndChoice()

            // The path the request took, step by step, with timings. Collected by .MessageHistory() above
            // and printed here, at Debug: raise the level of this module to see it for every request.
            .Log(MessageHistory.Format, LogLevel.Debug);
    }

    private static string? DecisionOf(IExchange exchange) => exchange.In.GetHeader<string>(SerialHeaders.Decision);
}
