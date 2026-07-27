using System.Text.RegularExpressions;
using Telegram.Bot.Types.Enums;
using redb.Route.Core;
using TgParseMode = global::Telegram.Bot.Types.Enums.ParseMode;

namespace redb.Route.Telegram;

/// <summary>
/// Typed options for <see cref="TelegramEndpoint"/>.
/// Bound from URI query parameters via reflection (EndpointOptions.BindFromUri).
///
/// Consumer URI:  telegram://receive?token=TOKEN
/// Producer URI:  telegram://send?token=TOKEN&amp;chatId=123456&amp;parseMode=HTML
/// Document URI:  telegram://document?token=TOKEN&amp;chatId=123456&amp;fileName=report.pdf
/// </summary>
public sealed class TelegramEndpointOptions : EndpointOptions
{
    // ── Auth ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bot token from @BotFather.
    /// Supports ${...} expressions: token=${env:TELEGRAM_TOKEN}
    /// </summary>
    [Sensitive]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Named <see cref="TelegramConnectionFactory"/> from the route registry. Lets the bot token
    /// live in the registry instead of the endpoint URI, so it never reaches logs or dashboards.
    /// </summary>
    public string? ConnectionFactory { get; set; }

    // ── Producer ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Target chat ID or username (@channelusername).
    /// Supports expressions: chatId=${header.telegram.chatId}
    /// </summary>
    public string? ChatId { get; set; }

    /// <summary>
    /// Parse mode for message text: HTML, Markdown, MarkdownV2.
    /// Default: plain text (null).
    /// </summary>
    public string? ParseMode { get; set; }

    /// <summary>
    /// Send message silently (no notification sound). Default false.
    /// </summary>
    public bool DisableNotification { get; set; }

    /// <summary>
    /// Caption for document/photo messages. Supports expressions.
    /// </summary>
    public string? Caption { get; set; }

    /// <summary>
    /// File name for document sends (when body is Stream or byte[]).
    /// Required for mode=document if body is a Stream.
    /// Supports expressions: fileName=${header.telegram.fileName}
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// When <c>true</c>, treats a string exchange body as a Telegram <c>file_id</c>
    /// in <c>document</c>/<c>photo</c> modes and reuses the file without re-uploading.
    /// Otherwise the body must be <c>Stream</c>, <c>byte[]</c>, an HTTP(S) URL,
    /// or the <c>telegram.fileId</c> header must be set. Default <c>false</c>.
    /// </summary>
    public bool BodyIsFileId { get; set; }

    /// <summary>
    /// Message to reply to. Supports expressions, resolved per message:
    /// <c>replyToMessageId=${header.telegram.messageId}</c> replies to the incoming
    /// message (the fluent sugar <c>.ReplyToIncoming()</c> emits exactly that).
    /// An explicit <c>telegram.replyToMessageId</c> header wins over this option.
    /// Applies to <c>send</c> / <c>document</c> / <c>photo</c> modes. Default: no reply.
    /// </summary>
    public string? ReplyToMessageId { get; set; }

    /// <summary>
    /// Message to edit / delete. Supports expressions, resolved per message:
    /// <c>messageId=${header.telegram.sentMessageId}</c> targets the message just sent
    /// by an upstream producer step. An explicit <c>telegram.messageId</c> header wins.
    /// Applies to <c>edit</c> / <c>delete</c> modes. Default: header required.
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// In <c>answer</c> mode, show the text as a modal alert instead of a toast
    /// at the top of the chat. Default <c>false</c>.
    /// </summary>
    public bool ShowAlert { get; set; }

    /// <summary>
    /// Per-send timeout in seconds for producer calls (send / document / photo / edit / delete / answer).
    /// Default 120. Range 1–600. Independent from the long-polling HTTP ceiling so large uploads over
    /// a slow link are not capped by the consumer's poll timeout.
    /// </summary>
    public int SendTimeoutSeconds { get; set; } = 120;

    // ── Consumer ──────────────────────────────────────────────────────────────
    //
    // The Telegram Bot API allows a single getUpdates long-poll stream per token. Telegram.Bot manages
    // that loop internally (started when OnMessage/OnUpdate handlers are attached) and does NOT expose a
    // long-poll timeout knob for the event-based receiver, so there is deliberately no consumer
    // timeoutSeconds option — advertising one would be a silent no-op. Parallel processing is achieved
    // with the EIP `.Threads(N)` on the route, not a native concurrency option (see README / CONCURRENCY.md).

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Token))
            throw new ArgumentException(
                "The 'token' parameter is required for Telegram endpoints.", nameof(Token));

        if (SendTimeoutSeconds is < 1 or > 600)
            throw new ArgumentOutOfRangeException(nameof(SendTimeoutSeconds),
                SendTimeoutSeconds, "Telegram send timeout must be between 1 and 600 seconds.");

        if (!TryParseParseMode(ParseMode, out _))
            throw new ArgumentException(
                $"Invalid parseMode '{ParseMode}'. Expected 'HTML', 'Markdown', 'MarkdownV2' or empty.",
                nameof(ParseMode));

        // A constant message-id option must be a message id; expressions are resolved per message.
        ValidateMessageIdOption(ReplyToMessageId, "replyToMessageId", nameof(ReplyToMessageId));
        ValidateMessageIdOption(MessageId, "messageId", nameof(MessageId));
    }

    private static void ValidateMessageIdOption(string? value, string uriName, string paramName)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !value.Contains("${", StringComparison.Ordinal)
            && !long.TryParse(value, out _))
            throw new ArgumentException(
                $"Invalid {uriName} '{value}'. Expected a message id or a ${{...}} expression.",
                paramName);
    }

    /// <summary>
    /// Maps a case-insensitive parse-mode string to <see cref="global::Telegram.Bot.Types.Enums.ParseMode"/>.
    /// Empty / null maps to <see cref="global::Telegram.Bot.Types.Enums.ParseMode.None"/> (plain text).
    /// Returns <see langword="false"/> for an unrecognised value so callers can throw instead of
    /// silently downgrading to plain text.
    /// </summary>
    internal static bool TryParseParseMode(string? raw, out TgParseMode mode)
    {
        mode = TgParseMode.None;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "html":       mode = TgParseMode.Html;       return true;
            case "markdown":   mode = TgParseMode.Markdown;   return true;
            case "markdownv2": mode = TgParseMode.MarkdownV2; return true;
            case "none":       mode = TgParseMode.None;       return true;
            default:           return false;
        }
    }

    // Env-only expression matcher — exchange is unavailable at endpoint start,
    // so full ExpressionResolver cannot be used for connect-time resolution.
    private static readonly Regex EnvExpressionRegex =
        new(@"\$\{env:([^}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Resolves <c>${env:NAME}</c> expressions inside a token value at endpoint
    /// start (when no <see cref="Abstractions.IExchange"/> is available). Any
    /// literal without <c>${</c> is returned unchanged.
    /// Throws <see cref="InvalidOperationException"/> if a referenced variable
    /// is not set — keeps misconfiguration loud instead of silently sending an
    /// unresolved literal to Telegram (HTTP 401).
    /// </summary>
    public static string ResolveTokenExpression(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains("${", StringComparison.Ordinal))
            return raw;

        return EnvExpressionRegex.Replace(raw, m =>
        {
            var name = m.Groups[1].Value;
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException(
                    $"Telegram: environment variable '{name}' referenced by token is not set.");
            return value;
        });
    }
}
