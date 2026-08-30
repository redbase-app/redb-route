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

        // Mini app payload (WebApp.sendData). Such a message has no Text, so the
        // data doubles as the exchange body — matching the "body = what the user sent"
        // contract of text messages and callback queries.
        if (msg.WebAppData is { } webApp)
        {
            message.Body = webApp.Data;
            message.Headers[TelegramHeaders.WebAppData] = webApp.Data;
            AddIfNotNull(message.Headers, TelegramHeaders.WebAppButtonText, webApp.ButtonText);
        }

        MapAttachment(msg, message);

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

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Exposes the attachment of an incoming message as headers: kind, <c>file_id</c> and
    /// whatever metadata Telegram sent with it.
    /// <para>
    /// Without this a file-bearing update is unreachable: the body carries
    /// <see cref="TgMessage.Text"/>, which a voice note does not have, so a route could see
    /// <em>that</em> a recording arrived (<see cref="TelegramHeaders.MessageType"/>) but had no
    /// way to fetch it. The body is deliberately left alone — a caption stays a header, so a
    /// captioned photo is not mistaken for a typed command.
    /// </para>
    /// </summary>
    private static void MapAttachment(TgMessage msg, IMessage message)
    {
        // Photo is a size ladder rather than a single file; the last entry is the largest.
        var photo = msg.Photo is { Length: > 0 } sizes ? sizes[^1] : null;

        var (kind, file) = msg switch
        {
            { Voice:     { } voice }     => ("voice",     (FileBase)voice),
            { Audio:     { } audio }     => ("audio",     audio),
            { VideoNote: { } note }      => ("videoNote", note),
            { Video:     { } video }     => ("video",     video),
            { Animation: { } animation } => ("animation", animation),
            { Document:  { } document }  => ("document",  document),
            { Sticker:   { } sticker }   => ("sticker",   sticker),
            _ when photo is not null     => ("photo",     photo),
            _                            => (null, null),
        };

        if (kind is null || file is null)
            return;

        message.Headers[TelegramHeaders.AttachmentKind]   = kind;
        message.Headers[TelegramHeaders.AttachmentFileId] = file.FileId;

        AddIfNotNull(message.Headers, TelegramHeaders.AttachmentFileSize, file.FileSize);
        AddIfNotNull(message.Headers, TelegramHeaders.AttachmentCaption,  msg.Caption);

        AddIfNotNull(message.Headers, TelegramHeaders.AttachmentMimeType, msg switch
        {
            { Voice:     { } voice }     => voice.MimeType,
            { Audio:     { } audio }     => audio.MimeType,
            { Video:     { } video }     => video.MimeType,
            { Animation: { } animation } => animation.MimeType,
            { Document:  { } document }  => document.MimeType,
            _ => null,
        });

        AddIfNotNull(message.Headers, TelegramHeaders.AttachmentDuration, msg switch
        {
            { Voice:     { } voice }     => voice.Duration,
            { Audio:     { } audio }     => audio.Duration,
            { Video:     { } video }     => video.Duration,
            { Animation: { } animation } => animation.Duration,
            { VideoNote: { } note }      => note.Duration,
            _ => (int?)null,
        });

        AddIfNotNull(message.Headers, TelegramHeaders.AttachmentFileName, msg switch
        {
            { Audio:    { } audio }    => audio.FileName,
            { Video:    { } video }    => video.FileName,
            { Document: { } document } => document.FileName,
            _ => null,
        });
    }

    private static void AddIfNotNull(IDictionary<string, object?> headers, string key, object? value)
    {
        if (value is not null) headers[key] = value;
    }
}
