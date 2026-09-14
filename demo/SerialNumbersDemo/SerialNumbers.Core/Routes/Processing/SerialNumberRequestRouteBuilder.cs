using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;
using redb.Route.RedbCore.Transactions;
using redb.Route.Transactions;
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
///   <item><c>IdempotentConsumer</c> around the transaction: the key is released when the transaction
///     fails, so a delivery that did not commit is processed again; once committed, it never is.</item>
///   <item>One redb transaction for everything the request writes: the message record, the request,
///     the serial numbers, the response and the outbox row.</item>
///   <item>The business decision is a branch, not an exception: a rejection is a normal answer.</item>
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

            .DoTry()
                .ValidateXsd(XmlSchemas.SerialNumberRequest)
                .Unmarshal<SerialNumberRequestXml>("application/xml")
            .DoCatch<ValidationException>()
                .ProcessWithRedb(IntakeRecorder.RecordInvalidAsync)
                .Log("${header.serials.partner}: ${header.serials.fileName} violates the schema, recorded as Invalid")
                .Stop()
            .EndTryCatch()

            .IdempotentConsumer(Header(SerialHeaders.MessageKey), RegistryNames.InboundFiles)
                // Suppress: redb opens its own explicit transaction; an ambient TransactionScope must not be open.
                .Transacted(TransactionPolicy.Suppress)
                    .BeginRedbTransaction()
                    .ProcessWithRedb(SerialRequestService.RegisterAsync)
                    .Choice()
                        .When(e => e.In.GetHeader<string>(SerialHeaders.Decision) == MessageStatuses.Accepted)
                            .ProcessWithRedb(SerialRequestService.AllocateAsync)
                        .Otherwise()
                            .ProcessWithRedb(SerialRequestService.RejectAsync)
                    .EndChoice()
                    .ProcessWithRedb(SerialRequestService.QueueResponseAsync)
                .EndTransaction()
            .EndIdempotentConsumer()

            .Log("${header.serials.partner}: request ${header.serials.requestId} ${header.serials.decision}");
    }
}
