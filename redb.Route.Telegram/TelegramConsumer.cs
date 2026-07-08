using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using TgMessage = Telegram.Bot.Types.Message;
using RouteMessage = redb.Route.Core.Message;

namespace redb.Route.Telegram;

/// <summary>
/// Telegram Bot long-polling consumer.
/// Subscribes to bot.OnMessage and bot.OnUpdate events (Telegram.Bot v21+ API).
/// Each received update is wrapped in an IExchange and passed to the processor pipeline.
///
/// Long polling starts automatically when TelegramBotClient is created with a token.
/// Cancelled when DrainableConsumer signals stop via pollCt.
/// </summary>
public sealed class TelegramConsumer : DrainableConsumer
{
    private readonly TelegramEndpoint _endpoint;
    private readonly TelegramEndpointOptions _options;
    private TelegramComponent? _component;
    private TelegramBotClient? _bot;
    private TelegramBotClient.OnMessageHandler? _onMessageHandler;
    private TelegramBotClient.OnUpdateHandler? _onUpdateHandler;
    private TelegramBotClient.OnErrorHandler? _onErrorHandler;

    private readonly object _handlerLock = new();
    private bool _handlersAttached;

    // Test seam: when false, RunAsync does not subscribe to the live bot (no network long-poll).
    // Tests drive updates via DeliverMessageForTest/DeliverUpdateForTest and errors via RaiseErrorForTest.
    internal bool AttachToLiveBot { get; set; } = true;

    // Test observability: set true when a fatal (401/409) polling error was handled.
    internal bool FatalPollingErrorDetected { get; private set; }

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => $"telegram:receive";

    /// <summary>Creates a Telegram long-polling consumer.</summary>
    public TelegramConsumer(TelegramEndpoint endpoint, IProcessor processor, TelegramEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options  = options  ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs synchronously inside <c>Start()</c>. Resolving the shared client and reserving the
    /// single-consumer slot here (rather than in the background <see cref="RunAsync"/>) makes the
    /// duplicate-consumer guard fail fast: a second consumer for the same token throws to the caller
    /// of <c>Start()</c> instead of dying silently on a background task.
    /// </remarks>
    protected override Task OnStarting(CancellationToken ct)
    {
        _component = _endpoint.Component as TelegramComponent
            ?? throw new InvalidOperationException(
                "Telegram consumer requires TelegramComponent as its owning component.");

        // Fail-fast: only one getUpdates stream per token is allowed (Bot API returns HTTP 409 otherwise).
        _component.RegisterConsumer(_options.Token);
        _bot = _component.GetOrCreateClient(_options.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Telegram.Bot v22 starts its single internal <c>getUpdates</c> loop when
    /// <c>bot.OnMessage</c>/<c>bot.OnUpdate</c> handlers are attached. The client is owned by
    /// <see cref="TelegramComponent"/> and shared across consumers/producers with the same token.
    /// Handlers are detached in <see cref="OnStopAccepting"/> (step 1 of the drain sequence) so no
    /// new update inflates the in-flight count during the drain window.
    /// </remarks>
    protected override async Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        var bot = _bot
            ?? throw new InvalidOperationException("Telegram consumer client was not initialised in OnStarting.");

        // Do NOT mutate bot.Timeout here. The HTTP timeout belongs to the shared HttpClient owned by
        // TelegramComponent (75s ceiling, covers the ~50s long-poll plus margin). Changing it here
        // would leak into producer sends that share the same client.

        _onErrorHandler = OnPollingError;

        _onMessageHandler = async (TgMessage msg, UpdateType type) =>
        {
            if (processingCt.IsCancellationRequested) return;
            await HandleMessageAsync(msg, type, processingCt).ConfigureAwait(false);
        };

        // OnUpdate catches everything: CallbackQuery, InlineQuery, etc.
        // All message-bearing update types are already handled via OnMessage — skip them
        // here to avoid double processing.
        _onUpdateHandler = async (Update update) =>
        {
            if (processingCt.IsCancellationRequested) return;
            if (IsMessageBearing(update.Type)) return;
            await HandleUpdateAsync(update, processingCt).ConfigureAwait(false);
        };

        if (AttachToLiveBot)
            AttachHandlers(bot);

        // Safety net for a Start()/Stop() race: if pollCt is already cancelled (or is cancelled while
        // this background task is still wiring up), detach immediately so nothing keeps polling.
        using var reg = pollCt.Register(DetachHandlers);

        Logger?.LogInformation(
            "Telegram long-polling started (sharedHttpTimeout={Http}s)", (int)bot.Timeout.TotalSeconds);

        // Block until pollCt is cancelled. Handlers do the work on the client's own polling thread.
        try
        {
            await Task.Delay(Timeout.Infinite, pollCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on stop */ }

        Logger?.LogInformation("Telegram long-polling stopped");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Step 1 of <see cref="DrainableConsumer"/>'s stop sequence: detach the event handlers BEFORE the
    /// drain so the internal getUpdates loop stops delivering. Otherwise every update arriving during
    /// the drain window would increment the in-flight counter and the drain could never reach zero.
    /// </remarks>
    protected override Task OnStopAccepting()
    {
        DetachHandlers();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task OnStopped()
    {
        DetachHandlers(); // idempotent safety net
        _component?.UnregisterConsumer(_options.Token);
        _bot = null;
        _component = null;
        _onMessageHandler = null;
        _onUpdateHandler = null;
        _onErrorHandler = null;
        return Task.CompletedTask;
    }

    // ── Handler attach / detach ───────────────────────────────────────────────

    private void AttachHandlers(TelegramBotClient bot)
    {
        lock (_handlerLock)
        {
            if (_handlersAttached) return;
            if (_onErrorHandler   is not null) bot.OnError   += _onErrorHandler;
            if (_onMessageHandler is not null) bot.OnMessage += _onMessageHandler;
            if (_onUpdateHandler  is not null) bot.OnUpdate  += _onUpdateHandler;
            _handlersAttached = true;
        }
    }

    /// <summary>
    /// Detaches all handlers from the shared client, which stops its internal getUpdates loop once the
    /// handler lists are empty. Idempotent and safe to call from any thread (Stop hooks, the pollCt
    /// registration, or a fatal <see cref="OnPollingError"/>).
    /// </summary>
    private void DetachHandlers()
    {
        lock (_handlerLock)
        {
            if (!_handlersAttached) return;
            var bot = _bot;
            if (bot is not null)
            {
                if (_onMessageHandler is not null) bot.OnMessage -= _onMessageHandler;
                if (_onUpdateHandler  is not null) bot.OnUpdate  -= _onUpdateHandler;
                if (_onErrorHandler   is not null) bot.OnError   -= _onErrorHandler;
            }
            _handlersAttached = false;
        }
    }

    /// <summary>
    /// Telegram.Bot polling / handler error callback. Fatal errors (401 revoked token, 409 conflicting
    /// webhook/second stream) detach the handlers to stop the loop — otherwise Telegram.Bot would retry
    /// <c>getUpdates</c> with no backoff and produce an API retry-storm plus a log flood. Transient
    /// errors are logged and left for Telegram.Bot's own retry.
    /// </summary>
    private Task OnPollingError(Exception ex, HandleErrorSource source)
    {
        if (IsFatal(ex))
        {
            FatalPollingErrorDetected = true;
            Logger?.LogCritical(ex,
                "Telegram: fatal polling error ({Source}) — stopping the getUpdates stream to avoid a retry storm.",
                source);
            DetachHandlers();
        }
        else
        {
            Logger?.LogError(ex,
                "Telegram transient polling error ({Source}); Telegram.Bot will retry.", source);
        }
        return Task.CompletedTask;
    }

    private static bool IsFatal(Exception ex) =>
        ex is ApiRequestException { ErrorCode: 401 or 409 };

    private static bool IsMessageBearing(UpdateType type) =>
        type is UpdateType.Message
             or UpdateType.EditedMessage
             or UpdateType.ChannelPost
             or UpdateType.EditedChannelPost
             or UpdateType.BusinessMessage
             or UpdateType.EditedBusinessMessage;

    // ── Test seams (exercised by redb.Route.Tests.Telegram; no network) ───────

    internal Task DeliverMessageForTest(TgMessage msg, UpdateType type, CancellationToken ct) =>
        HandleMessageAsync(msg, type, ct);

    internal Task DeliverUpdateForTest(Update update, CancellationToken ct) =>
        HandleUpdateAsync(update, ct);

    internal Task RaiseErrorForTest(Exception ex, HandleErrorSource source) =>
        OnPollingError(ex, source);

    // ── Message handler ───────────────────────────────────────────────────────

    private async Task HandleMessageAsync(
        TgMessage msg,
        UpdateType updateType,
        CancellationToken ct)
    {
        using var activity = RouteActivitySource.Source.StartActivity(
            $"telegram.{updateType} receive", ActivityKind.Consumer);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "telegram");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", updateType.ToString());
            activity.SetTag("messaging.telegram.chat.id", msg.Chat.Id);
            activity.SetTag("messaging.telegram.chat.type", msg.Chat.Type.ToString());
            activity.SetTag("messaging.telegram.message.id", msg.MessageId);
        }

        var exchange = CreateMessageExchange(msg, updateType);
        IncrementInflight();
        try
        {
            // Endpoint statistics (RecordMessageIn/Out/Error) are recorded by the
            // framework's StatisticsProcessor which wraps Processor. Matches the
            // Kafka/RabbitMQ pattern — no explicit Record* calls here.
            await Processor.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown mid-processing — not a message error.
        }
        catch (Exception ex)
        {
            // At-most-once: Telegram.Bot advances the update offset as soon as the update is dispatched,
            // so there is no redelivery. Route-level error handling (OnException / dead-letter) already
            // ran inside Processor.Process; anything reaching here is terminal. Log it as a dropped
            // message rather than re-throwing into the polling loop (which would only re-log the same error).
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Logger?.LogError(ex,
                "Telegram consumer: unhandled processor error for message {MessageId} in chat {ChatId} — " +
                "message dropped (at-most-once, no redelivery).",
                msg.MessageId, msg.Chat.Id);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
            DecrementInflight();
        }
    }

    // ── Update handler (CallbackQuery, etc.) ──────────────────────────────────

    private async Task HandleUpdateAsync(
        Update update,
        CancellationToken ct)
    {
        var exchange = CreateUpdateExchange(update);
        if (exchange is null) return;

        using var activity = RouteActivitySource.Source.StartActivity(
            $"telegram.{update.Type} receive", ActivityKind.Consumer);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "telegram");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", update.Type.ToString());
            activity.SetTag("messaging.telegram.update.id", update.Id);

            if (update.CallbackQuery is { } cb)
            {
                activity.SetTag("messaging.telegram.callback_query.id", cb.Id);
                if (cb.Message?.Chat is { } chat)
                {
                    activity.SetTag("messaging.telegram.chat.id", chat.Id);
                    activity.SetTag("messaging.telegram.chat.type", chat.Type.ToString());
                }
            }
        }

        IncrementInflight();
        try
        {
            await Processor.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown mid-processing — not a message error.
        }
        catch (Exception ex)
        {
            // At-most-once (see HandleMessageAsync): terminal error, message dropped, no redelivery.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Logger?.LogError(ex,
                "Telegram consumer: unhandled processor error for update {UpdateId} — " +
                "message dropped (at-most-once, no redelivery).", update.Id);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
            DecrementInflight();
        }
    }

    // ── Exchange factories ────────────────────────────────────────────────────

    private Exchange CreateMessageExchange(TgMessage msg, UpdateType updateType)
    {
        var routeMessage = new RouteMessage();
        TelegramUpdateMapper.MapMessage(msg, updateType, routeMessage);
        var exchange = Exchange.Create(routeMessage, _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOnly;
        return exchange;
    }

    private Exchange? CreateUpdateExchange(Update update)
    {
        var routeMessage = new RouteMessage();
        if (!TelegramUpdateMapper.MapUpdate(update, routeMessage))
        {
            Logger?.LogDebug("Telegram consumer: skipping update type {Type}", update.Type);
            return null;
        }

        var exchange = Exchange.Create(routeMessage, _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOnly;
        return exchange;
    }
}
