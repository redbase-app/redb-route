namespace redb.Route.Telegram;

/// <summary>
/// Header names written to IExchange by <see cref="TelegramConsumer"/>.
/// Use these constants to read Telegram metadata inside route processors.
/// </summary>
public static class TelegramHeaders
{
    // ── Update / common ───────────────────────────────────────────────────────

    /// <summary>Update ID (long). Unique per bot, monotonically increasing.</summary>
    public const string UpdateId = "telegram.updateId";

    /// <summary>Update type string: "Message", "EditedMessage", "CallbackQuery", etc.</summary>
    public const string UpdateType = "telegram.updateType";

    // ── Message ───────────────────────────────────────────────────────────────

    /// <summary>Telegram message ID (int) within the chat.</summary>
    public const string MessageId = "telegram.messageId";

    /// <summary>Message type: "Text", "Photo", "Document", "Sticker", etc.</summary>
    public const string MessageType = "telegram.messageType";

    /// <summary>Raw text of the message (null for non-text messages).</summary>
    public const string Text = "telegram.text";

    /// <summary>Chat ID (long). Use this to reply via the producer.</summary>
    public const string ChatId = "telegram.chatId";

    /// <summary>Chat type: "Private", "Group", "Supergroup", "Channel".</summary>
    public const string ChatType = "telegram.chatType";

    // ── Sender ────────────────────────────────────────────────────────────────

    /// <summary>Sender user ID (long).</summary>
    public const string UserId = "telegram.userId";

    /// <summary>Sender first name.</summary>
    public const string FirstName = "telegram.firstName";

    /// <summary>Sender last name (nullable).</summary>
    public const string LastName = "telegram.lastName";

    /// <summary>Sender @username (nullable, without @).</summary>
    public const string Username = "telegram.username";

    /// <summary>Sender language code, e.g. "ru", "en".</summary>
    public const string LanguageCode = "telegram.languageCode";

    // ── Callback query ────────────────────────────────────────────────────────

    /// <summary>Callback query ID — pass to bot.AnswerCallbackQuery(...).</summary>
    public const string CallbackQueryId = "telegram.callbackQueryId";

    /// <summary>Callback data string set on the InlineKeyboardButton.</summary>
    public const string CallbackData = "telegram.callbackData";

    // ── Producer controls ─────────────────────────────────────────────────────

    /// <summary>
    /// Reply markup to attach to a sent message.
    /// Set to an <c>InlineKeyboardMarkup</c> or <c>ReplyKeyboardMarkup</c> instance
    /// from the <c>Telegram.Bot.Types.ReplyMarkups</c> namespace.
    /// Only used by the <c>send</c> producer mode.
    /// </summary>
    public const string ReplyMarkup = "telegram.replyMarkup";

    /// <summary>
    /// Message ID to reply to in <c>send</c> mode.
    /// Set to an <c>int</c> (or parseable string). When present, the sent message
    /// will appear as a reply to the referenced message in the chat.
    /// </summary>
    public const string ReplyToMessageId = "telegram.replyToMessageId";

    /// <summary>
    /// Per-message override for <see cref="TelegramEndpointOptions.ParseMode"/>.
    /// Values: <c>"HTML"</c>, <c>"Markdown"</c>, <c>"MarkdownV2"</c>. Case-insensitive.
    /// </summary>
    public const string ParseMode = "telegram.parseMode";

    /// <summary>
    /// Per-message override for <see cref="TelegramEndpointOptions.DisableNotification"/>.
    /// Accepts <c>bool</c> or string parseable as bool.
    /// </summary>
    public const string DisableNotification = "telegram.disableNotification";

    /// <summary>
    /// Explicit Telegram file_id for <c>document</c>/<c>photo</c> producer modes.
    /// When present, the producer reuses the file already hosted on Telegram
    /// instead of uploading the exchange body.
    /// </summary>
    public const string FileId = "telegram.fileId";

    /// <summary>File name to use when the exchange body is a <see cref="System.IO.Stream"/> or <c>byte[]</c>.</summary>
    public const string FileName = "telegram.fileName";

    // ── Producer results (set on the outgoing exchange after publish) ─────────

    /// <summary>
    /// Set by the producer after a successful <c>send</c>/<c>document</c>/<c>photo</c>/<c>edit</c>
    /// to the resulting <see cref="global::Telegram.Bot.Types.Message.MessageId"/> (<c>int</c>).
    /// Allows downstream steps to edit or delete the message just produced without
    /// a separate round-trip.
    /// </summary>
    public const string SentMessageId = "telegram.sentMessageId";

    // ── Internal ──────────────────────────────────────────────────────────────

    /// <summary>Returns true if the key is a redb.Route.Telegram internal header.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith("telegram.", StringComparison.OrdinalIgnoreCase);
}
