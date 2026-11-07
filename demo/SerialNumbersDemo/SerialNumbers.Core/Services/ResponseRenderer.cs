using redb.Core;
using redb.Route.Abstractions;
using SerialNumbers.Core.Integration.Xml;
using SerialNumbers.Domain.Entities;

namespace SerialNumbers.Core.Services;

/// <summary>Turns an outbox row into the XML response the partner receives.</summary>
public static class ResponseRenderer
{
    public static async Task RenderAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        // The sql: consumer copies the columns of the polled row into headers.
        var responseId = Convert.ToInt64(exchange.In.Headers["response_object_id"]);
        var response = await redb.LoadAsync<SerialNumberResponse>(responseId, cancellationToken: ct)
            ?? throw new InvalidOperationException($"Response object {responseId} was not found.");

        var props = response.Props;
        var xml = new SerialNumberResponseXml
        {
            RequestId = props.RequestId,
            Gtin = props.Gtin,
            Status = props.Accepted ? "Accepted" : "Rejected",
            Reason = props.RejectionReason,
            Quantity = props.Quantity,
        };

        if (props.AllocationId is long allocationId)
        {
            var serials = await SerialNumberAllocator.SerialNumbersOfAsync(redb, allocationId, ct);
            xml.SerialNumbers = serials.Select(s => s.ToString("D12")).ToList();
        }

        exchange.In.Body = xml;
        exchange.In.Headers[SerialHeaders.FileName] = ArchivePaths.ResponseFileName(props.RequestId, responseId);
    }
}
