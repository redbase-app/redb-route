using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core.Services;

/// <summary>Writes the <see cref="InboundMessage"/> record of a delivered file.</summary>
public static class IntakeRecorder
{
    /// <summary>The file is broken XML or violates the schema.</summary>
    public static async Task RecordInvalidAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        var reason = exchange.Exception?.Message ?? "The file is not well-formed XML.";
        await redb.SaveAsync(Create(exchange, MessageStatuses.Invalid, reason), ct);
    }

    /// <summary>A well-formed message this module has no route for.</summary>
    public static async Task RecordParkedAsync(IRedbService redb, IExchange exchange, CancellationToken ct)
    {
        var type = exchange.In.GetHeader<string>(SerialHeaders.MessageType);
        await redb.SaveAsync(Create(exchange, MessageStatuses.Parked, $"No route for message type '{type}'."), ct);
    }

    /// <summary>The record built from the headers the inbound routes set.</summary>
    public static RedbObject<InboundMessage> Create(IExchange exchange, string status, string? error)
    {
        var partner = exchange.In.GetHeader<string>(SerialHeaders.Partner) ?? "";
        var fileName = exchange.In.GetHeader<string>(SerialHeaders.FileName) ?? "";

        return new RedbObject<InboundMessage>
        {
            name = $"{partner}/{fileName}",
            Props = new InboundMessage
            {
                PartnerCode = partner,
                Transport = exchange.In.GetHeader<string>(SerialHeaders.Transport) ?? "",
                FileName = fileName,
                MessageType = exchange.In.GetHeader<string>(SerialHeaders.MessageType),
                ArchivePath = exchange.In.GetHeader<string>(SerialHeaders.ArchivePath) ?? "",
                ReceivedAt = DateTimeOffset.UtcNow,
                Status = status,
                Error = error,
            },
        };
    }
}
