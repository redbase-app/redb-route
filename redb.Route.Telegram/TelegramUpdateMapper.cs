using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using redb.Route.Abstractions;
using TgMessage = Telegram.Bot.Types.Message;

namespace redb.Route.Telegram;

/// <summary>
/// Maps Telegram <see cref="Update"/> / <see cref="TgMessage"/> objects to
/// <see cref="IMessage"/> headers using the canonical <see cref="TelegramHeaders"/> constants.
/// <para>
/// Shared between <see cref="TelegramConsumer"/> (long-polling) and
/// <see cref="TelegramUpdateUnpackProcessor"/> (webhook) so both paths
/// produce identical header semantics.
/// </para>
/// </summary>
public static class TelegramUpdateMapper
{
    /// <summary>
    /// Populates <paramref name="message"/> body and headers from a Telegram message update.
    /// Call this when the update carries a <see cref="TgMessage"/> payload.
    /// </summary>
    /// <param name="msg">Telegram message.</param>
    /// <param name="updateType">Update type (e.g. <see cref="UpdateType.Message"/> or EditedMessage).</param>
    /// <param name="message">Route message to populate.</param>
    public static void MapMessage(TgMessage msg, UpdateType updateType, IMessage message)
    {
        message.Body = msg.Text ?? string.Empty;

        message.Headers[TelegramHeaders.UpdateType]  = updateType.ToString();
        message.Headers[TelegramHeaders.MessageType] = msg.Type.ToString();
        message.Headers[TelegramHeaders.MessageId]   = msg.MessageId;
        AddIfNotNull(message.Headers, TelegramHeaders.Text, msg.Text);

        message.Headers[TelegramHeaders.ChatId]   = msg.Chat.Id;
        message.Headers[TelegramHeaders.ChatType] = msg.Chat.Type.ToString();

        if (msg.From is { } from)
        {
            message.Headers[TelegramHeaders.UserId]    = from.Id;
            message.Headers[TelegramHeaders.FirstName] = from.FirstName;
            AddIfNotNull(message.Headers, TelegramHeaders.LastName,     from.LastName);
            AddIfNotNull(message.Headers, TelegramHeaders.Username,     from.Username);
            AddIfNotNull(message.Headers, TelegramHeaders.LanguageCode, from.LanguageCode);
        }
    }

    /// <summary>
    /// Populates <paramref name="message"/> body and headers from a generic <see cref="Update"/>.
    /// Returns <see langword="true"/> when the update type is handled; <see langword="false"/>
    /// for unrecognised / unsupported types (caller should skip the exchange).
    /// </summary>
    /// <param name="update">Telegram update.</param>
    /// <param name="message">Route message to populate.</param>
    /// <returns><see langword="true"/> if the update was mapped; <see langword="false"/> to skip.</returns>
    public static bool MapUpdate(Update update, IMessage message)
    {
        message.Headers[TelegramHeaders.UpdateId]   = update.Id;
        message.Headers[TelegramHeaders.UpdateType] = update.Type.ToString();

        switch (update.Type)
        {
            // ── Message-bearing updates — delegate to MapMessage ──────────────
            case UpdateType.Message         when update.Message         is { } m: MapMessage(m, UpdateType.Message,         message); return true;
            case UpdateType.EditedMessage   when update.EditedMessage   is { } m: MapMessage(m, UpdateType.EditedMessage,   message); return true;
            case UpdateType.ChannelPost     when update.ChannelPost     is { } m: MapMessage(m, UpdateType.ChannelPost,     message); return true;
            case UpdateType.EditedChannelPost when update.EditedChannelPost is { } m: MapMessage(m, UpdateType.EditedChannelPost, message); return true;

            // ── CallbackQuery ─────────────────────────────────────────────────
            case UpdateType.CallbackQuery when update.CallbackQuery is { } cb:
                message.Body = cb.Data ?? string.Empty;
                message.Headers[TelegramHeaders.CallbackQueryId] = cb.Id;
                message.Headers[TelegramHeaders.UserId]          = cb.From.Id;
                message.Headers[TelegramHeaders.FirstName]       = cb.From.FirstName;
                AddIfNotNull(message.Headers, TelegramHeaders.CallbackData, cb.Data);
                AddIfNotNull(message.Headers, TelegramHeaders.ChatId,       cb.Message?.Chat.Id);
                AddIfNotNull(message.Headers, TelegramHeaders.Username,     cb.From.Username);
                return true;

            default:
                return false;
        }
    }

    // ── Internal helper ───────────────────────────────────────────────────────

    private static void AddIfNotNull(IDictionary<string, object?> headers, string key, object? value)
    {
        if (value is not null) headers[key] = value;
    }
}
