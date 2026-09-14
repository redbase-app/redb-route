using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using SerialNumbers.Core.Integration.Xml;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core.Services;

/// <summary>
/// The steps of a serial number request. Each one is a <c>ProcessWithRedb</c> step of the request
/// route; all of them run inside one redb transaction the route opens, so they either all commit
/// or leave no trace.
/// </summary>
public static class SerialRequestService
{
    /// <summary>
    /// Records the message and the request and takes the decision. Sets the
    /// <see cref="SerialHeaders.Decision"/> header the route branches on.
    /// </summary>
    public static async Task RegisterAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        var xml = (SerialNumberRequestXml)exchange.In.Body!;
        var partner = exchange.In.GetHeader<string>(SerialHeaders.Partner)!;

        // The GTIN is the product's unique key: a system-field lookup, no Props scan.
        var product = await redb.Query<Product>()
            .WhereRedb(o => o.ValueUnique == xml.Gtin)
            .FirstOrDefaultAsync();

        var earlier = await redb.Query<SerialNumberRequest>()
            .Where(r => r.PartnerCode == partner && r.RequestId == xml.RequestId)
            .Take(1)
            .ToListAsync();
        var isDuplicate = earlier.Count > 0;

        var allocatedThisYear = product is null
            ? 0
            : await SerialNumberAllocator.AllocatedThisYearAsync(redb, xml.Gtin, DateTime.UtcNow.Year, ct);

        var rejection = SerialRequestPolicy.Evaluate(product?.Props, xml.Quantity, allocatedThisYear, isDuplicate);
        var status = rejection is null ? MessageStatuses.Accepted : MessageStatuses.Rejected;

        var messageId = await redb.SaveAsync(IntakeRecorder.Create(exchange, status, error: rejection), ct);

        var request = new RedbObject<SerialNumberRequest>
        {
            name = $"{partner}/{xml.RequestId}",
            // The partner's request id is unique per partner. A concurrent twin fails on this key,
            // rolls back and is answered as a duplicate when the file is picked up again.
            ValueUnique = isDuplicate ? null : $"{partner}/{xml.RequestId}",
            Props = new SerialNumberRequest
            {
                RequestId = xml.RequestId,
                PartnerCode = partner,
                Gtin = xml.Gtin,
                Quantity = xml.Quantity,
                Status = status,
                RejectionReason = rejection,
                Message = new RedbObject<InboundMessage> { id = messageId },
            },
        };
        await redb.SaveAsync(request, ct);

        exchange.Properties[SerialProperties.Request] = request;
        exchange.In.Headers[SerialHeaders.RequestId] = xml.RequestId;
        exchange.In.Headers[SerialHeaders.Decision] = status;
    }

    /// <summary>Accepted: reserves the serial numbers and writes the response.</summary>
    public static async Task AllocateAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        var request = RequestOf(exchange);
        var first = await SerialNumberAllocator.AllocateAsync(redb, request.Props, request.Id, DateTime.UtcNow.Year, ct);

        var response = ResponseTo(request);
        response.Props.Accepted = true;
        response.Props.FirstSerial = first;
        response.Props.LastSerial = first + request.Props.Quantity - 1;
        await redb.SaveAsync(response, ct);

        exchange.Properties[SerialProperties.Response] = response;
    }

    /// <summary>Rejected: writes a response carrying the reason.</summary>
    public static async Task RejectAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        var request = RequestOf(exchange);

        var response = ResponseTo(request);
        response.Props.Accepted = false;
        response.Props.RejectionReason = request.Props.RejectionReason;
        await redb.SaveAsync(response, ct);

        exchange.Properties[SerialProperties.Response] = response;
    }

    /// <summary>Puts the response into the outbox, in the same transaction as everything above.</summary>
    public static async Task QueueResponseAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        var response = (RedbObject<SerialNumberResponse>)exchange.Properties[SerialProperties.Response]!;
        await OutboxWriter.EnqueueAsync(redb, response.Props.PartnerCode, response.Id, ct);
    }

    private static RedbObject<SerialNumberRequest> RequestOf(IExchange exchange) =>
        (RedbObject<SerialNumberRequest>)exchange.Properties[SerialProperties.Request]!;

    private static RedbObject<SerialNumberResponse> ResponseTo(RedbObject<SerialNumberRequest> request) => new()
    {
        name = $"{request.Props.PartnerCode}/{request.Props.RequestId}",
        Props = new SerialNumberResponse
        {
            RequestId = request.Props.RequestId,
            PartnerCode = request.Props.PartnerCode,
            Gtin = request.Props.Gtin,
            Quantity = request.Props.Quantity,
            Request = new RedbObject<SerialNumberRequest> { id = request.Id },
        },
    };
}
