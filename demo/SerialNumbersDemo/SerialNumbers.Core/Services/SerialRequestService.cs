using System.Globalization;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using SerialNumbers.Core.Integration.Xml;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core.Services;

/// <summary>
/// The steps of a serial number request. Each one is a <c>ProcessWithRedb</c> step inside the request
/// route's <c>.Transacted()</c> block, so they either all commit or leave no trace.
/// </summary>
public static class SerialRequestService
{
    /// <summary>
    /// Records the message and the request and takes the decision, or, for the release of a request on
    /// hold, decides that request again. Sets the <see cref="SerialHeaders.Decision"/> header the route
    /// branches on.
    /// </summary>
    public static async Task RegisterAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        if (exchange.In.Headers.TryGetValue(SerialHeaders.ReleaseOf, out var releaseOf) && releaseOf is not null)
        {
            await ResumeAsync(redb, exchange, Convert.ToInt64(releaseOf, CultureInfo.InvariantCulture), ct);
            return;
        }

        var xml = (SerialNumberRequestXml)exchange.In.Body!;
        var partner = exchange.In.GetHeader<string>(SerialHeaders.Partner)!;

        var product = await ProductAsync(redb, xml.Gtin);

        var earlier = await redb.Query<SerialNumberRequest>()
            .Where(r => r.PartnerCode == partner && r.RequestId == xml.RequestId)
            .Take(1)
            .ToListAsync();
        var isDuplicate = earlier.Count > 0;

        var allocatedThisYear = await AllocatedThisYearAsync(redb, product, ct);
        var decision = SerialRequestPolicy.Decide(product?.Props, xml.Quantity, allocatedThisYear, isDuplicate);

        var messageId = await redb.SaveAsync(IntakeRecorder.Create(exchange, decision.Status, error: decision.RejectionReason), ct);

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
                Status = decision.Status,
                RejectionReason = decision.RejectionReason,
                Message = new RedbObject<InboundMessage> { id = messageId },
            },
        };
        await redb.SaveAsync(request, ct);

        exchange.Properties[SerialProperties.Request] = request;
        exchange.In.Headers[SerialHeaders.RequestId] = xml.RequestId;
        exchange.In.Headers[SerialHeaders.Decision] = decision.Status;
    }

    /// <summary>Accepted: reserves the serial numbers and writes the response.</summary>
    public static async Task AllocateAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        var request = RequestOf(exchange);
        var allocationId = await SerialNumberAllocator.AllocateAsync(redb, request.Props, request.Id, DateTime.UtcNow.Year, ct);

        var response = ResponseTo(request);
        response.Props.Accepted = true;
        response.Props.AllocationId = allocationId;
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

    /// <summary>
    /// The release of a request on hold: the same request object is decided again, against the
    /// product and the quota as they are now. No new request, no new message record.
    /// </summary>
    private static async Task ResumeAsync(IRedbService redb, IExchange exchange, long requestObjectId, CancellationToken ct)
    {
        var request = await redb.LoadAsync<SerialNumberRequest>(requestObjectId, cancellationToken: ct)
            ?? throw new InvalidOperationException($"Request object {requestObjectId} was not found.");
        exchange.In.Headers[SerialHeaders.RequestId] = request.Props.RequestId;

        if (request.Props.Status != MessageStatuses.OnHold)
        {
            exchange.In.Headers[SerialHeaders.Decision] = ReleaseOutcomes.AlreadyDecided;
            return;
        }

        var product = await ProductAsync(redb, request.Props.Gtin);
        var allocatedThisYear = await AllocatedThisYearAsync(redb, product, ct);
        var decision = SerialRequestPolicy.Decide(product?.Props, request.Props.Quantity, allocatedThisYear, isDuplicate: false);

        if (decision.Status != MessageStatuses.OnHold)
        {
            request.Props.Status = decision.Status;
            request.Props.RejectionReason = decision.RejectionReason;
            await redb.SaveAsync(request, ct);

            // Saved after the request, so the message record ends with the new status.
            var messageId = request.Props.Message?.Id
                ?? throw new InvalidOperationException($"Request object {requestObjectId} has no inbound message.");
            var message = await redb.LoadAsync<InboundMessage>(messageId, cancellationToken: ct)
                ?? throw new InvalidOperationException($"Inbound message object {messageId} was not found.");
            message.Props.Status = decision.Status;
            message.Props.Error = decision.RejectionReason;
            await redb.SaveAsync(message, ct);
        }

        exchange.Properties[SerialProperties.Request] = request;
        exchange.In.Headers[SerialHeaders.Decision] = decision.Status;
    }

    /// <summary>The GTIN is the product's unique key: a system-field lookup, no Props scan.</summary>
    private static Task<RedbObject<Product>?> ProductAsync(IRedbService redb, string gtin) =>
        redb.Query<Product>()
            .WhereRedb(o => o.ValueUnique == gtin)
            .FirstOrDefaultAsync();

    /// <summary>
    /// What was allocated this year, for an active product only. The read takes the range lock that keeps
    /// two requests from passing the quota together; a request that waits or is rejected does not need it.
    /// </summary>
    private static async Task<long> AllocatedThisYearAsync(IRedbService redb, RedbObject<Product>? product, CancellationToken ct) =>
        product?.Props.Status == ProductStatuses.Active
            ? await SerialNumberAllocator.AllocatedThisYearAsync(redb, product.Props.Gtin, DateTime.UtcNow.Year, ct)
            : 0;

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
