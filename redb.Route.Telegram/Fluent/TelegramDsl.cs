using System.Text;
using System.Web;
using Telegram.Bot.Types;
using redb.Route.Abstractions;

namespace redb.Route.Telegram.Fluent;

/// <summary>
/// Entry point for the Telegram fluent DSL.
/// <example><code>
/// // Consumer (long polling)
/// From(Tg.Receive(token))
///
/// // Producer (text message)
/// .To(Tg.Send(token).ChatId(chatId).ParseMode("HTML"))
///
/// // Producer (document)
/// .To(Tg.Document(token).ChatId(chatId).FileName("report.pdf").Caption("Daily report"))
/// </code></example>
/// </summary>
public static class Tg
{
    /// <summary>Creates a long-polling consumer endpoint.</summary>
    public static TgBuilder Receive(string token) => new TgBuilder("receive", token);

    /// <summary>Creates a long-polling consumer with expression token.</summary>
    public static TgBuilder Receive(IExpression token) => new TgBuilder("receive", token.ToTemplateString());

    /// <summary>Creates a text message producer endpoint.</summary>
    public static TgBuilder Send(string token) => new TgBuilder("send", token);

    /// <summary>Creates a text message producer with expression token.</summary>
    public static TgBuilder Send(IExpression token) => new TgBuilder("send", token.ToTemplateString());

    /// <summary>Creates a document producer endpoint (body must be Stream or byte[]).</summary>
    public static TgBuilder Document(string token) => new TgBuilder("document", token);

    /// <summary>Creates a document producer with expression token.</summary>
    public static TgBuilder Document(IExpression token) => new TgBuilder("document", token.ToTemplateString());

    /// <summary>Creates a photo producer endpoint (body must be Stream or byte[]).</summary>
    public static TgBuilder Photo(string token) => new TgBuilder("photo", token);

    /// <summary>Creates a photo producer with expression token.</summary>
    public static TgBuilder Photo(IExpression token) => new TgBuilder("photo", token.ToTemplateString());

    /// <summary>Answers a callback query (removes the loading spinner on an inline button).</summary>
    public static TgBuilder Answer(string token) => new TgBuilder("answer", token);

    /// <summary>Answers a callback query using an expression token.</summary>
    public static TgBuilder Answer(IExpression token) => new TgBuilder("answer", token.ToTemplateString());

    /// <summary>Edits the text (and optionally markup) of an existing message.
    /// Requires header <c>telegram.messageId</c> at runtime.</summary>
    public static TgBuilder Edit(string token) => new TgBuilder("edit", token);

    /// <summary>Edits an existing message using an expression token.</summary>
    public static TgBuilder Edit(IExpression token) => new TgBuilder("edit", token.ToTemplateString());

    /// <summary>Deletes a message. Requires headers <c>telegram.chatId</c>
    /// and <c>telegram.messageId</c> at runtime.</summary>
    public static TgBuilder Delete(string token) => new TgBuilder("delete", token);

    /// <summary>Deletes a message using an expression token.</summary>
    public static TgBuilder Delete(IExpression token) => new TgBuilder("delete", token.ToTemplateString());

    // ── Token-less overloads: the bot token comes from a registered
    //    TelegramConnectionFactory via .ConnectionFactory("name") ──────────────

    /// <summary>Long-polling consumer; token supplied by <c>.ConnectionFactory(name)</c>.</summary>
    public static TgBuilder Receive() => new TgBuilder("receive", string.Empty);

    /// <summary>Text message producer; token supplied by <c>.ConnectionFactory(name)</c>.</summary>
    public static TgBuilder Send() => new TgBuilder("send", string.Empty);

    /// <summary>Document producer; token supplied by <c>.ConnectionFactory(name)</c>.</summary>
    public static TgBuilder Document() => new TgBuilder("document", string.Empty);

    /// <summary>Photo producer; token supplied by <c>.ConnectionFactory(name)</c>.</summary>
    public static TgBuilder Photo() => new TgBuilder("photo", string.Empty);

    /// <summary>Callback-answer producer; token supplied by <c>.ConnectionFactory(name)</c>.</summary>
    public static TgBuilder Answer() => new TgBuilder("answer", string.Empty);

    /// <summary>Edit-message producer; token supplied by <c>.ConnectionFactory(name)</c>.</summary>
    public static TgBuilder Edit() => new TgBuilder("edit", string.Empty);

    /// <summary>Delete-message producer; token supplied by <c>.ConnectionFactory(name)</c>.</summary>
    public static TgBuilder Delete() => new TgBuilder("delete", string.Empty);
}

/// <summary>
/// Fluent builder for Telegram endpoint URIs.
/// Builds a URI consumed by <see cref="TelegramComponent.CreateEndpoint"/>.
/// Pattern mirrors KafkaDsl / RabbitMQ Rabbit builder.
/// </summary>
public sealed class TgBuilder
{
    private readonly string _mode;
    private readonly string _token;
    private string? _chatId;
    private string? _parseMode;
    private string? _caption;
    private string? _fileName;
    private bool    _disableNotification;
    private bool    _bodyIsFileId;
    private string? _replyToMessageId;
    private string? _messageId;
    private bool    _showAlert;
    private int?    _sendTimeoutSeconds;

    private string? _connectionFactory;

    internal TgBuilder(string mode, string token)
    {
        _mode  = mode;
        _token = token;
    }

    /// <summary>
    /// References a named <see cref="TelegramConnectionFactory"/> from the route registry instead of
    /// putting the bot token in the URI. Use with the token-less mode overloads, e.g.
    /// <c>Tg.Receive().ConnectionFactory("support-bot")</c>.
    /// </summary>
    public TgBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    // ── Common ─────────────────────────────────────────────────────────────────

    /// <summary>Target chat ID (long) or @channelusername.</summary>
    public TgBuilder ChatId(long id) { _chatId = id.ToString(); return this; }

    /// <summary>Target chat ID from an expression (e.g. Header("telegram.chatId")).</summary>
    public TgBuilder ChatId(IExpression expr) { _chatId = expr.ToTemplateString(); return this; }

    /// <summary>Parse mode: "HTML", "Markdown", "MarkdownV2". Default: plain text.</summary>
    public TgBuilder ParseMode(string mode) { _parseMode = mode; return this; }

    /// <summary>Send silently (no sound notification). Default false.</summary>
    public TgBuilder DisableNotification(bool value = true) { _disableNotification = value; return this; }

    /// <summary>Reply to a specific message id (send/document/photo).</summary>
    public TgBuilder ReplyTo(long messageId) { _replyToMessageId = messageId.ToString(); return this; }

    /// <summary>Reply target from an expression, resolved per message
    /// (e.g. <c>Header("myApp.threadRootId")</c>).</summary>
    public TgBuilder ReplyTo(IExpression expr) { _replyToMessageId = expr.ToTemplateString(); return this; }

    /// <summary>
    /// Replies to the incoming message. Sugar for <see cref="ReplyTo(IExpression)"/> with the
    /// <c>${header.telegram.messageId}</c> expression — the id the consumer put on the exchange.
    /// An explicit <c>telegram.replyToMessageId</c> header wins over this option.
    /// <example><code>
    /// From(Tg.Receive(token))
    ///     .To(Tg.Send(token).ChatId(Header(TelegramHeaders.ChatId)).ReplyToIncoming());
    /// </code></example>
    /// </summary>
    public TgBuilder ReplyToIncoming() { _replyToMessageId = "${header." + TelegramHeaders.MessageId + "}"; return this; }

    /// <summary>Message to edit / delete, by id.</summary>
    public TgBuilder MessageId(long id) { _messageId = id.ToString(); return this; }

    /// <summary>Message to edit / delete, from an expression resolved per message
    /// (e.g. <c>Header(TelegramHeaders.SentMessageId)</c> to target the message just sent).</summary>
    public TgBuilder MessageId(IExpression expr) { _messageId = expr.ToTemplateString(); return this; }

    /// <summary>In <c>answer</c> mode, show the text as a modal alert instead of a toast. Default false.</summary>
    public TgBuilder ShowAlert(bool value = true) { _showAlert = value; return this; }

    // ── Document / photo ──────────────────────────────────────────────────────

    /// <summary>File name for document sends.</summary>
    public TgBuilder FileName(string name) { _fileName = name; return this; }

    /// <summary>File name from an expression (e.g. Header("telegram.fileName")).</summary>
    public TgBuilder FileName(IExpression expr) { _fileName = expr.ToTemplateString(); return this; }

    /// <summary>Caption for document/photo messages.</summary>
    public TgBuilder Caption(string text) { _caption = text; return this; }

    /// <summary>Caption from an expression.</summary>
    public TgBuilder Caption(IExpression expr) { _caption = expr.ToTemplateString(); return this; }

    /// <summary>Treat a string exchange body as a Telegram <c>file_id</c> (document/photo) instead of uploading.</summary>
    public TgBuilder BodyIsFileId(bool value = true) { _bodyIsFileId = value; return this; }

    // ── Producer ──────────────────────────────────────────────────────────────

    /// <summary>Per-send timeout in seconds for producer calls (1–600). Default 120. Not used by <c>receive</c>.</summary>
    public TgBuilder Timeout(int seconds) { _sendTimeoutSeconds = seconds; return this; }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>Builds the telegram:// URI string.</summary>
    public string Build()
    {
        var sb  = new StringBuilder();
        var sep = '?';

        sb.Append("telegram://");
        sb.Append(_mode);

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value));
            sep = '&';
        }

        void AppendIf(string key, string? value) { if (value != null) Append(key, value); }

        // A token-less builder relies on connectionFactory= supplying the credential.
        if (!string.IsNullOrEmpty(_token)) Append("token", _token);
        AppendIf("connectionFactory", _connectionFactory);
        AppendIf("chatId", _chatId);
        AppendIf("parseMode", _parseMode);
        AppendIf("caption", _caption);
        AppendIf("fileName", _fileName);
        AppendIf("replyToMessageId", _replyToMessageId);
        AppendIf("messageId", _messageId);

        if (_disableNotification)
            Append("disableNotification", "true");

        if (_bodyIsFileId)
            Append("bodyIsFileId", "true");

        if (_showAlert)
            Append("showAlert", "true");

        if (_sendTimeoutSeconds.HasValue)
            Append("sendTimeoutSeconds", _sendTimeoutSeconds.Value.ToString());

        return sb.ToString();
    }

    /// <summary>Allows passing TgBuilder directly to From() / To() without calling Build().</summary>
    public static implicit operator string(TgBuilder builder) => builder.Build();
}

/// <summary>
/// Extension methods for using the Telegram webhook unpack pipeline inside a route DSL.
/// </summary>
public static class TelegramRouteExtensions
{
    /// <summary>
    /// Deserializes a raw Telegram webhook JSON body into an <see cref="Update"/> object
    /// and populates all <see cref="TelegramHeaders"/> — equivalent to a long-polling
    /// <see cref="TelegramConsumer"/> exchange.
    /// <example><code>
    /// From(Http.Listen("/tg/webhook"))
    ///     .UnpackTelegramUpdate()
    ///     .To("direct://tg-dispatch");
    /// </code></example>
    /// </summary>
    /// <param name="route">The current route definition.</param>
    /// <returns>The route definition for chaining.</returns>
    public static IRouteDefinition UnpackTelegramUpdate(this IRouteDefinition route) =>
        route
            .Unmarshal(typeof(TelegramUpdateSerializer), typeof(Update))
            .Process(new TelegramUpdateUnpackProcessor());

    /// <summary>
    /// Attaches an inline keyboard to the next produced message by setting the
    /// <see cref="TelegramHeaders.ReplyMarkup"/> header. Sugar over
    /// <c>SetHeader(TelegramHeaders.ReplyMarkup, TgKeyboard.Inline(build))</c>.
    /// <example><code>
    /// From(Tg.Receive(token))
    ///     .SetBody("Choose:")
    ///     .WithInlineKeyboard(k => k
    ///         .Row(r => r.Callback("✅ Yes", "act:yes").Callback("❌ No", "act:no")))
    ///     .To(Tg.Send(token).ChatId(Header(TelegramHeaders.ChatId)));
    /// </code></example>
    /// </summary>
    /// <param name="route">The current route definition.</param>
    /// <param name="build">Callback that builds the inline keyboard.</param>
    /// <returns>The route definition for chaining.</returns>
    public static IRouteDefinition WithInlineKeyboard(
        this IRouteDefinition route, Action<InlineKeyboardBuilder> build) =>
        route.SetHeader(TelegramHeaders.ReplyMarkup, TgKeyboard.Inline(build));

    /// <summary>
    /// Attaches a reply keyboard (buttons below the input field) to the next produced
    /// message by setting the <see cref="TelegramHeaders.ReplyMarkup"/> header.
    /// <example><code>
    /// .WithReplyKeyboard(k => k
    ///     .Row(r => r.Button("📋 Menu").Button("❓ Help"))
    ///     .Resize().OneTime())
    /// </code></example>
    /// </summary>
    /// <param name="route">The current route definition.</param>
    /// <param name="build">Callback that builds the reply keyboard.</param>
    /// <returns>The route definition for chaining.</returns>
    public static IRouteDefinition WithReplyKeyboard(
        this IRouteDefinition route, Action<ReplyKeyboardBuilder> build) =>
        route.SetHeader(TelegramHeaders.ReplyMarkup, TgKeyboard.Reply(build));

    /// <summary>
    /// Removes the current custom reply keyboard on the next produced message by setting the
    /// <see cref="TelegramHeaders.ReplyMarkup"/> header to a <c>ReplyKeyboardRemove</c>.
    /// </summary>
    /// <param name="route">The current route definition.</param>
    /// <param name="selective">Remove the keyboard only for specific users (see Bot API docs).</param>
    /// <returns>The route definition for chaining.</returns>
    public static IRouteDefinition WithoutKeyboard(
        this IRouteDefinition route, bool selective = false) =>
        route.SetHeader(TelegramHeaders.ReplyMarkup, TgKeyboard.Remove(selective));
}
