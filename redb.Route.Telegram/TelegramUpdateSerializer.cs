using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using redb.Route.Abstractions;

namespace redb.Route.Telegram;

/// <summary>
/// Deserializes a Telegram <see cref="Update"/> from a raw JSON byte array received
/// via webhook (HTTP POST from Telegram servers).
/// <para>
/// Uses <see cref="JsonBotAPI.Options"/> — the same <see cref="JsonSerializerOptions"/>
/// that <c>Telegram.Bot</c> uses internally, ensuring correct handling of polymorphic
/// <see cref="Update"/> fields (e.g. <c>MaybeInaccessibleMessage</c>).
/// </para>
/// <para>
/// Serialization (<see cref="Serialize{T}"/>) is intentionally not supported:
/// the Telegram Bot API is inbound-only in the webhook path.
/// </para>
/// </summary>
public sealed class TelegramUpdateSerializer : IMessageSerializer
{
    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) =>
        throw new NotSupportedException(
            $"{nameof(TelegramUpdateSerializer)} is receive-only. " +
            "Use Telegram.Bot client methods to send messages.");

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data)
    {
        if (typeof(T) != typeof(Update))
            throw new NotSupportedException(
                $"{nameof(TelegramUpdateSerializer)} only deserializes {nameof(Update)}. " +
                $"Requested type: {typeof(T).Name}");

        return (T?)(object?)DeserializeUpdate(data);
    }

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type)
    {
        if (type != typeof(Update))
            throw new NotSupportedException(
                $"{nameof(TelegramUpdateSerializer)} only deserializes {nameof(Update)}. " +
                $"Requested type: {type.Name}");

        return DeserializeUpdate(data);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static Update DeserializeUpdate(byte[] data)
    {
        var update = JsonSerializer.Deserialize<Update>(data, JsonBotAPI.Options);
        return update ?? throw new InvalidOperationException(
            "Telegram webhook: failed to deserialize Update — JSON body was null or empty.");
    }
}
