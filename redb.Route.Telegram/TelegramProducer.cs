using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using TgMessage = Telegram.Bot.Types.Message;

namespace redb.Route.Telegram;

/// <summary>
/// Telegram Bot producer.
/// Supported modes: send, document, photo, answer, edit, delete.
///
/// ChatId is resolved (in priority order):
///   1. Header telegram.chatId on the exchange
///   2. Options.ChatId (from URI or DSL)
///
/// On successful send/document/photo/edit the resulting <c>Message.MessageId</c>
/// is written back to the exchange as header <see cref="TelegramHeaders.SentMessageId"/>.
/// </summary>
public sealed class TelegramProducer : ConnectableProducer
{
    private readonly TelegramEndpoint _endpoint;
    private readonly TelegramEndpointOptions _options;
    private ITelegramBotClient? _bot;
    private ITelegramBotClient? _testClient;

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"telegram:{_endpoint.Mode}";

    /// <summary>Creates a Telegram producer.</summary>
    public TelegramProducer(TelegramEndpoint endpoint, TelegramEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options  = options  ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct)
    {
        // Test seam: a mock ITelegramBotClient injected via UseTestClient bypasses the component.
        if (_testClient is not null)
        {
            _bot = _testClient;
            return Task.CompletedTask;
        }

        // Client is owned and pooled by TelegramComponent (shared per resolved token).
        // Producer/consumer for the same token get the same instance.
        if (_endpoint.Component is not TelegramComponent component)
            throw new InvalidOperationException(
                "Telegram producer requires TelegramComponent as its owning component.");

        _bot = component.GetOrCreateClient(_options.Token);
        Logger?.LogInformation("Telegram producer connected (mode={Mode})", _endpoint.Mode);
        return Task.CompletedTask;
    }

    /// <summary>Test seam: inject a mock <see cref="ITelegramBotClient"/> used instead of the component client.</summary>
    internal void UseTestClient(ITelegramBotClient bot) => _testClient = bot;

    /// <inheritdoc />
    protected override Task DisconnectAsync(CancellationToken ct)
    {
        // Ownership of the client (and its HttpClient) stays with TelegramComponent.
        // Releasing our reference is enough here.
        _bot = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();
        // Capture the client locally: DisconnectAsync nulls _bot on Stop(), and a concurrent Process
        // must not dereference a null field (shutdown race).
        var bot = _bot
            ?? throw new InvalidOperationException(
                $"{ProducerName}: producer client is unavailable (stopped concurrently?).");

        using var activity = RouteActivitySource.Source.StartActivity(
            $"telegram.{_endpoint.Mode} publish", ActivityKind.Producer);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "telegram");
            activity.SetTag("messaging.operation", "publish");
            activity.SetTag("messaging.telegram.mode", _endpoint.Mode);
        }

        // Per-request timeout linked to the caller ct. Uses the dedicated producer send timeout
        // (SendTimeoutSeconds) — independent from the long-poll HTTP ceiling on the shared client, so a
        // large upload over a slow link is not capped at the ~50s long-poll window.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.SendTimeoutSeconds));
        var reqCt = cts.Token;

        ChatId? chatIdForLog = null;
        try
        {
            // answer mode uses callbackQueryId — no chatId needed
            if (_endpoint.Mode == "answer")
            {
                await AnswerCallbackQueryAsync(bot, exchange, activity, reqCt).ConfigureAwait(false);
            }
            else
            {
                var chatId = ResolveChatId(exchange);
                chatIdForLog = chatId;
                activity?.SetTag("messaging.destination.name", chatId.ToString());
                activity?.SetTag("messaging.telegram.chat.id", chatId.ToString());

                switch (_endpoint.Mode)
                {
                    case "document":
                        await SendDocumentAsync(bot, exchange, chatId, reqCt).ConfigureAwait(false);
                        break;

                    case "photo":
                        await SendPhotoAsync(bot, exchange, chatId, reqCt).ConfigureAwait(false);
                        break;

                    case "edit":
                        await EditMessageAsync(bot, exchange, chatId, reqCt).ConfigureAwait(false);
                        break;

                    case "delete":
                        await DeleteMessageAsync(bot, exchange, chatId, reqCt).ConfigureAwait(false);
                        break;

                    default: // "send"
                        await SendMessageAsync(bot, exchange, chatId, reqCt).ConfigureAwait(false);
                        break;
                }
            }

            // Record on the producer endpoint (matches S3Producer / SqsProducer). The consumer-side
            // StatisticsProcessor does not wrap the telegram:// producer path, so MessagesOut would
            // otherwise stay at 0.
            _endpoint.RecordMessageOut();
        }
        catch (ApiRequestException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogPublishDiagnostics(ex, exchange, chatIdForLog);
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    // ── Rate-limit (429) retry ────────────────────────────────────────────────

    /// <summary>
    /// Telegram enforces per-chat/global flood limits and answers with HTTP 429 plus a
    /// <c>retry_after</c> hint. Bot API contract: wait that long and retry. Failing to wait (and
    /// letting the route retry immediately) escalates to a temporary token ban, so this is the one
    /// obligation a Telegram producer must implement. Retries are bounded and honour the request
    /// cancellation token; <c>retry_after</c> is clamped to [1, 60]s to avoid an unbounded stall.
    /// </summary>
    private async Task<T> WithRateLimitRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429 && attempt < maxRetries)
            {
                var retryAfter = Math.Clamp(ex.Parameters?.RetryAfter ?? 1, 1, 60);
                Logger?.LogWarning(
                    "Telegram: rate-limited (429) on mode={Mode}; waiting {RetryAfter}s before retry {Attempt}/{Max}.",
                    _endpoint.Mode, retryAfter, attempt + 1, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(retryAfter), ct).ConfigureAwait(false);
            }
        }
    }

    private Task WithRateLimitRetryAsync(Func<CancellationToken, Task> action, CancellationToken ct) =>
        WithRateLimitRetryAsync<object?>(async c => { await action(c).ConfigureAwait(false); return null; }, ct);

    // ── Send modes ────────────────────────────────────────────────────────────

    private async Task SendMessageAsync(
        ITelegramBotClient bot,
        IExchange exchange,
        ChatId chatId,
        CancellationToken ct)
    {
        var text      = ResolveText(exchange);
        var parseMode = ResolveParseMode(exchange);
        var markup    = ResolveReplyMarkup(exchange);
        var replyToId = ResolveReplyToMessageId(exchange);
        var disableNotification = ResolveDisableNotification(exchange);
        var replyParams = replyToId.HasValue
            ? new ReplyParameters { MessageId = replyToId.Value }
            : null;

        var result = await WithRateLimitRetryAsync(c => bot.SendMessage(
            chatId,
            text,
            parseMode: parseMode,
            replyMarkup: markup,
            replyParameters: replyParams,
            disableNotification: disableNotification,
            cancellationToken: c
        ), ct).ConfigureAwait(false);

        SetSentMessageId(exchange, result);
        Logger?.LogDebug("Telegram: sent message {MessageId} to chat {ChatId}", result.MessageId, chatId);
    }

    private async Task SendDocumentAsync(
        ITelegramBotClient bot,
        IExchange exchange,
        ChatId chatId,
        CancellationToken ct)
    {
        var inputFile = ResolveInputFile(exchange);
        var caption   = ResolveCaption(exchange);
        var parseMode = ResolveParseMode(exchange);
        var disableNotification = ResolveDisableNotification(exchange);
        var replyToId = ResolveReplyToMessageId(exchange);
        var replyParams = replyToId.HasValue
            ? new ReplyParameters { MessageId = replyToId.Value }
            : null;

        var result = await WithRateLimitRetryAsync(c => bot.SendDocument(
            chatId,
            inputFile,
            caption: caption,
            parseMode: parseMode,
            replyParameters: replyParams,
            replyMarkup: ResolveReplyMarkup(exchange),
            disableNotification: disableNotification,
            cancellationToken: c
        ), ct).ConfigureAwait(false);

        SetSentMessageId(exchange, result);
        Logger?.LogDebug("Telegram: sent document {MessageId} to chat {ChatId}", result.MessageId, chatId);
    }

    private async Task SendPhotoAsync(
        ITelegramBotClient bot,
        IExchange exchange,
        ChatId chatId,
        CancellationToken ct)
    {
        var inputFile = ResolveInputFile(exchange);
        var caption   = ResolveCaption(exchange);
        var parseMode = ResolveParseMode(exchange);
        var disableNotification = ResolveDisableNotification(exchange);
        var replyToId = ResolveReplyToMessageId(exchange);
        var replyParams = replyToId.HasValue
            ? new ReplyParameters { MessageId = replyToId.Value }
            : null;

        var result = await WithRateLimitRetryAsync(c => bot.SendPhoto(
            chatId,
            inputFile,
            caption: caption,
            parseMode: parseMode,
            replyParameters: replyParams,
            replyMarkup: ResolveReplyMarkup(exchange),
            disableNotification: disableNotification,
            cancellationToken: c
        ), ct).ConfigureAwait(false);

        SetSentMessageId(exchange, result);
        Logger?.LogDebug("Telegram: sent photo {MessageId} to chat {ChatId}", result.MessageId, chatId);
    }

    private async Task AnswerCallbackQueryAsync(
        ITelegramBotClient bot,
        IExchange exchange,
        Activity? activity,
        CancellationToken ct)
    {
        if (!exchange.In.Headers.TryGetValue(TelegramHeaders.CallbackQueryId, out var v) || v is null)
            throw new InvalidOperationException(
                "Telegram answer: header 'telegram.callbackQueryId' is required. " +
                "Use Tg.Answer(token) after receiving a CallbackQuery update.");

        var callbackQueryId = v.ToString()!;
        // Optional notification text. Coerce non-string bodies rather than silently dropping them.
        var text = exchange.In.Body switch
        {
            null       => null,
            string s   => s,
            var other  => other.ToString()
        };

        activity?.SetTag("messaging.destination.name", callbackQueryId);
        activity?.SetTag("messaging.telegram.callback_query.id", callbackQueryId);

        await WithRateLimitRetryAsync(
            c => bot.AnswerCallbackQuery(callbackQueryId, text,
                showAlert: ResolveShowAlert(exchange), cancellationToken: c), ct)
            .ConfigureAwait(false);

        Logger?.LogDebug("Telegram: answered callback query {CallbackQueryId}", callbackQueryId);
    }

    /// <remarks>
    /// The Telegram Bot API method <c>editMessageText</c> does not accept a
    /// <c>disable_notification</c> parameter — editing an existing message never
    /// triggers a notification. The <c>DisableNotification</c> option and the
    /// <c>telegram.disableNotification</c> header are intentionally ignored here.
    /// </remarks>
    private async Task EditMessageAsync(
        ITelegramBotClient bot,
        IExchange exchange,
        ChatId chatId,
        CancellationToken ct)
    {
        var messageId = ResolveMessageId(exchange);
        var text      = ResolveText(exchange);
        var parseMode = ResolveParseMode(exchange);
        // EditMessageText only accepts InlineKeyboardMarkup, not ReplyKeyboard.
        InlineKeyboardMarkup? markup = null;
        if (exchange.In.Headers.TryGetValue(TelegramHeaders.ReplyMarkup, out var v) && v is not null)
        {
            markup = v as InlineKeyboardMarkup
                ?? throw new InvalidOperationException(
                    $"Telegram edit: header '{TelegramHeaders.ReplyMarkup}' must be an " +
                    $"InlineKeyboardMarkup, but was '{v.GetType().Name}'.");
        }

        var result = await WithRateLimitRetryAsync(c => bot.EditMessageText(
                chatId, messageId, text,
                parseMode: parseMode,
                replyMarkup: markup,
                cancellationToken: c), ct)
             .ConfigureAwait(false);

        SetSentMessageId(exchange, result);
        Logger?.LogDebug("Telegram: edited message {MessageId} in chat {ChatId}", messageId, chatId);
    }

    private async Task DeleteMessageAsync(
        ITelegramBotClient bot,
        IExchange exchange,
        ChatId chatId,
        CancellationToken ct)
    {
        var messageId = ResolveMessageId(exchange);

        await WithRateLimitRetryAsync(
            c => bot.DeleteMessage(chatId, messageId, cancellationToken: c), ct)
            .ConfigureAwait(false);

        Logger?.LogDebug("Telegram: deleted message {MessageId} in chat {ChatId}", messageId, chatId);
    }

    // ── Resolution helpers ────────────────────────────────────────────────────

    private ChatId ResolveChatId(IExchange exchange)
    {
        // 1. Header set by consumer or upstream processor
        if (exchange.In.Headers.TryGetValue(TelegramHeaders.ChatId, out var headerValue)
            && headerValue is not null)
        {
            switch (headerValue)
            {
                case long l:   return new ChatId(l);
                case int  i:   return new ChatId(i);
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    return new ChatId(parsed);
                case string s: return new ChatId(s); // @channelusername
            }

            try
            {
                return new ChatId(Convert.ToInt64(headerValue, CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new InvalidOperationException(
                    $"Telegram producer: header '{TelegramHeaders.ChatId}' value '{headerValue}' " +
                    "is not a valid chat id (expected long or '@channelusername').", ex);
            }
        }

        // 2. Options (from URI or DSL builder)
        if (!string.IsNullOrWhiteSpace(_options.ChatId))
        {
            var resolved = _options.ResolveOption(_options.ChatId, exchange);
            if (string.IsNullOrWhiteSpace(resolved))
                throw new InvalidOperationException(
                    "Telegram producer: chatId expression resolved to an empty value. " +
                    "Check that the header or environment variable referenced in ChatId is set.");
            if (long.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return new ChatId(id);
            return new ChatId(resolved); // @channelusername
        }

        throw new InvalidOperationException(
            "Telegram producer: no chatId. Set header 'telegram.chatId' or use " +
            ".ChatId(...) in the fluent builder.");
    }

    private string ResolveText(IExchange exchange)
    {
        var body = exchange.In.Body;
        var text = body switch
        {
            string s => s,
            byte[] b => System.Text.Encoding.UTF8.GetString(b),
            null     => null,
            _        => body.ToString()
        };

        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException(
                $"Telegram {_endpoint.Mode}: exchange.In.Body must contain non-empty text " +
                "(string or UTF-8 byte[]). Telegram rejects empty message text with HTTP 400.");

        return text;
    }

    private ParseMode ResolveParseMode(IExchange exchange)
    {
        // Header takes precedence over options for per-message overrides.
        string? raw = null;
        if (exchange.In.Headers.TryGetValue(TelegramHeaders.ParseMode, out var h) && h is not null)
            raw = h.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            raw = _options.ParseMode;

        // Fail loud on a typo (e.g. "Markdwn") instead of silently downgrading to plain text and
        // shipping raw *bold* / <b> markup to the user.
        if (!TelegramEndpointOptions.TryParseParseMode(raw, out var mode))
            throw new InvalidOperationException(
                $"Telegram {_endpoint.Mode}: invalid parseMode '{raw}'. Expected HTML, Markdown, MarkdownV2 or empty.");
        return mode;
    }

    private bool ResolveDisableNotification(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(TelegramHeaders.DisableNotification, out var v) && v is not null)
        {
            return v switch
            {
                bool b => b,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => _options.DisableNotification
            };
        }
        return _options.DisableNotification;
    }

    private string? ResolveCaption(IExchange exchange)
    {
        // Header takes precedence over options for per-message overrides.
        if (exchange.In.Headers.TryGetValue(TelegramHeaders.Caption, out var h) && h is string s)
            return s;
        if (!string.IsNullOrWhiteSpace(_options.Caption))
            return _options.ResolveOption(_options.Caption, exchange);
        return null;
    }

    private bool ResolveShowAlert(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(TelegramHeaders.ShowAlert, out var v) && v is not null)
        {
            return v switch
            {
                bool b => b,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => _options.ShowAlert
            };
        }
        return _options.ShowAlert;
    }

    private InputFile ResolveInputFile(IExchange exchange)
    {
        // 1. Explicit file_id header — reuse a file already hosted on Telegram.
        if (exchange.In.Headers.TryGetValue(TelegramHeaders.FileId, out var fileIdHeader)
            && fileIdHeader is string fileId
            && !string.IsNullOrWhiteSpace(fileId))
        {
            return InputFile.FromFileId(fileId);
        }

        var body     = exchange.In.Body;
        var fileName = ResolveFileName(exchange);

        switch (body)
        {
            case Stream s:
                return InputFile.FromStream(s, fileName ?? "file");

            case byte[] bytes:
                return InputFile.FromStream(new MemoryStream(bytes), fileName ?? "file");

            // URL string is checked before the file_id opt-in because URLs are always
            // preferred when they parse as absolute http(s).
            case string s when Uri.TryCreate(s, UriKind.Absolute, out var uri)
                            && (uri.Scheme == "http" || uri.Scheme == "https"):
                return InputFile.FromUri(uri);

            // Explicit URI opt-in: bodyIsFileId=true treats the string body as a file_id.
            case string s when _options.BodyIsFileId:
                return InputFile.FromFileId(s);

            case string:
                throw new InvalidOperationException(
                    $"Telegram {_endpoint.Mode}: string body is not a valid input. " +
                    "Provide a Stream, byte[], or HTTP(S) URL. " +
                    $"To reuse an existing Telegram file, set header '{TelegramHeaders.FileId}' " +
                    "or add 'bodyIsFileId=true' to the URI.");

            default:
                throw new InvalidOperationException(
                    $"Telegram {_endpoint.Mode}: unsupported body type '{body?.GetType().Name ?? "null"}'. " +
                    "Expected Stream, byte[], HTTP(S) URL string, or 'telegram.fileId' header.");
        }
    }

    private string? ResolveFileName(IExchange exchange)
    {
        if (!string.IsNullOrWhiteSpace(_options.FileName))
            return _options.ResolveOption(_options.FileName, exchange);

        if (exchange.In.Headers.TryGetValue(TelegramHeaders.FileName, out var h) && h is string s)
            return s;

        return null;
    }

    private static ReplyMarkup? ResolveReplyMarkup(IExchange exchange)
    {
        if (!exchange.In.Headers.TryGetValue(TelegramHeaders.ReplyMarkup, out var v) || v is null)
            return null;
        if (v is ReplyMarkup markup)
            return markup;
        throw new InvalidOperationException(
            $"Telegram: header '{TelegramHeaders.ReplyMarkup}' must be a Telegram.Bot ReplyMarkup " +
            $"(InlineKeyboardMarkup / ReplyKeyboardMarkup / ...), but was '{v.GetType().Name}'.");
    }

    private int? ResolveReplyToMessageId(IExchange exchange)
    {
        // Explicit header always wins (per-message override, same as chatId).
        if (exchange.In.Headers.TryGetValue(TelegramHeaders.ReplyToMessageId, out var v) && v is not null)
            return ToMessageId(v);

        if (!string.IsNullOrWhiteSpace(_options.ReplyToMessageId))
        {
            var resolved = _options.ResolveOption(_options.ReplyToMessageId, exchange);
            // Empty resolution: the referenced header is absent on this update type
            // (e.g. .ReplyToIncoming() on an update without telegram.messageId) —
            // send unthreaded rather than fail the exchange.
            if (string.IsNullOrWhiteSpace(resolved))
                return null;
            if (int.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return id;
            throw new InvalidOperationException(
                $"Telegram {_endpoint.Mode}: replyToMessageId resolved to '{resolved}', " +
                "which is not a valid message id.");
        }

        return null;
    }

    private static int? ToMessageId(object v) => v switch
    {
        int i    => i,
        long l   => (int)l,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) => id,
        _        => null
    };

    private int ResolveMessageId(IExchange exchange)
    {
        // Explicit header always wins (per-message override, same as chatId).
        if (exchange.In.Headers.TryGetValue(TelegramHeaders.MessageId, out var v) && v is not null)
            return ToMessageId(v) ?? throw new InvalidOperationException(
                $"Telegram producer: cannot convert 'telegram.messageId' value '{v}' to int.");

        if (!string.IsNullOrWhiteSpace(_options.MessageId))
        {
            var resolved = _options.ResolveOption(_options.MessageId, exchange);
            if (int.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return id;
            throw new InvalidOperationException(
                $"Telegram {_endpoint.Mode}: messageId resolved to '{resolved}', " +
                "which is not a valid message id.");
        }

        throw new InvalidOperationException(
            "Telegram producer: no message id for edit/delete. Set header 'telegram.messageId' " +
            "or use .MessageId(...) in the fluent builder.");
    }

    private static void SetSentMessageId(IExchange exchange, TgMessage? result)
    {
        if (result is null) return;
        exchange.In.Headers[TelegramHeaders.SentMessageId] = result.MessageId;
    }

    private void LogPublishDiagnostics(ApiRequestException ex, IExchange exchange, ChatId? chatId)
    {
        var body     = exchange.In.Body;
        var bodySize = body switch
        {
            string s => s.Length,
            byte[] b => b.Length,
            Stream s when s.CanSeek => (int)Math.Min(int.MaxValue, s.Length),
            _        => -1
        };

        Logger?.LogError(ex,
            "Telegram publish failed: mode={Mode} chatId={ChatId} parseMode={ParseMode} " +
            "bodySize={BodySize} errorCode={ErrorCode} retryAfter={RetryAfter}",
            _endpoint.Mode,
            chatId?.ToString() ?? "(unresolved)",
            _options.ParseMode ?? "None",
            bodySize,
            ex.ErrorCode,
            ex.Parameters?.RetryAfter);
    }
}
