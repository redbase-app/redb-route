using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core.Services;

/// <summary>A request on hold, with what it takes to send its archived file through the request route again.</summary>
public sealed record HeldRequest(
    long RequestObjectId,
    string RequestId,
    string PartnerCode,
    string Transport,
    string FileName,
    string MessageType,
    string ArchivePath);

/// <summary>
/// Releasing requests on hold when their product becomes active. Nothing is asked of the partner: the file
/// they sent is in the archive, and it goes through the request route once more.
/// </summary>
public static class HeldRequests
{
    /// <summary>The requests on hold for the product in the exchange, when the product is now active.</summary>
    public static async Task FindAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        var held = new List<HeldRequest>();

        if (exchange.In.GetHeader<string>(SerialHeaders.ProductStatus) == ProductStatuses.Active)
        {
            var gtin = exchange.In.GetHeader<string>(SerialHeaders.Gtin)!;

            var requests = await redb.Query<SerialNumberRequest>()
                .Where(r => r.Gtin == gtin && r.Status == MessageStatuses.OnHold)
                .ToListAsync();

            if (requests.Count > 0)
            {
                // The inbound messages of all of them in one query, not one per request.
                var messageIds = requests.Select(MessageIdOf).ToHashSet();
                var messages = await redb.Query<InboundMessage>()
                    .WhereRedb(o => messageIds.Contains(o.Id))
                    .ToListAsync();
                var messageById = messages.ToDictionary(m => m.Id, m => m.Props);

                foreach (var request in requests.OrderBy(r => r.Id))
                {
                    var message = messageById[MessageIdOf(request)];
                    held.Add(new HeldRequest(
                        request.Id,
                        request.Props.RequestId,
                        message.PartnerCode,
                        message.Transport,
                        message.FileName,
                        message.MessageType ?? "",
                        message.ArchivePath));
                }
            }
        }

        exchange.In.Body = held;
        exchange.In.Headers[SerialHeaders.HeldCount] = held.Count;
    }

    /// <summary>
    /// Puts the archived file of one request on hold into the exchange, with the headers the inbound
    /// routes set for a delivered file, and marks it as the release of that request.
    /// </summary>
    public static async Task LoadArchivedFileAsync(IExchange exchange, string archiveDirectory, CancellationToken ct)
    {
        var held = (HeldRequest)exchange.In.Body!;

        exchange.In.Body = await File.ReadAllTextAsync(Path.Combine(archiveDirectory, held.ArchivePath), ct);

        var headers = exchange.In.Headers;
        headers[SerialHeaders.Partner] = held.PartnerCode;
        headers[SerialHeaders.Transport] = held.Transport;
        headers[SerialHeaders.FileName] = held.FileName;
        headers[SerialHeaders.MessageType] = held.MessageType;
        headers[SerialHeaders.ArchivePath] = held.ArchivePath;
        headers[SerialHeaders.RequestId] = held.RequestId;
        headers[SerialHeaders.ReleaseOf] = held.RequestObjectId;
    }

    private static long MessageIdOf(RedbObject<SerialNumberRequest> request) =>
        request.Props.Message?.Id
        ?? throw new InvalidOperationException($"Request object {request.Id} has no inbound message.");
}
