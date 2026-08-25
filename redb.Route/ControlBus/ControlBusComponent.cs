using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.ControlBus;

/// <summary>The control-bus command family.</summary>
public enum ControlBusCommand
{
    /// <summary><c>controlbus:route</c> — act on a route by id (Apache Camel parity).</summary>
    Route,
    /// <summary><c>controlbus:language:&lt;lang&gt;</c> — evaluate an expression against the context (Camel parity).</summary>
    Language,
    /// <summary><c>controlbus:notify</c> — <b>consume</b> route/context lifecycle events as messages (redb extension beyond Camel).</summary>
    Notify
}

/// <summary>Exchange header keys set by the <c>controlbus:notify</c> event consumer.</summary>
public static class ControlBusHeaders
{
    /// <summary>Event name: RouteStarted / RouteStopped / RouteSuspending / RouteErrored / ContextStarting / ContextStarted / ContextStopping / ContextStopped / ExchangeTimedOut.</summary>
    public const string Event = "controlbus.event";
    /// <summary>Affected route id (for route/exchange events).</summary>
    public const string RouteId = "controlbus.routeId";
    /// <summary>Event timestamp (UTC).</summary>
    public const string Timestamp = "controlbus.timestamp";
    /// <summary>Error message (RouteErrored only).</summary>
    public const string Error = "controlbus.error";
    /// <summary>Exchange id (ExchangeTimedOut only).</summary>
    public const string ExchangeId = "controlbus.exchangeId";
    /// <summary>Elapsed milliseconds (ExchangeTimedOut only).</summary>
    public const string ElapsedMs = "controlbus.elapsedMs";
}

/// <summary>Actions for the <c>route</c> command (Apache Camel parity).</summary>
public enum ControlBusAction
{
    /// <summary>Start the route.</summary>
    Start,
    /// <summary>Stop the route (consumer removed; route stays registered).</summary>
    Stop,
    /// <summary>Suspend the route. Same effect as <see cref="Stop"/> in redb (route stays registered).</summary>
    Suspend,
    /// <summary>Resume a stopped/suspended route (policy-aware).</summary>
    Resume,
    /// <summary>Stop then start after <c>restartDelay</c>.</summary>
    Restart,
    /// <summary>Return the route status on the message body.</summary>
    Status,
    /// <summary>Return route statistics (all routes when <c>routeId</c> is omitted) on the message body.</summary>
    Stats,
    /// <summary>Stop the route and mark it failed (Errored).</summary>
    Fail
}

// ── Component ────────────────────────────────────────────────────────────────

/// <summary>
/// Control Bus EIP component (Apache Camel <c>controlbus:</c> parity). Producer-only — you send <b>to</b> it
/// to manage routes at runtime. Modelled on the <see cref="redb.Route.Validation.ValidatorComponent"/>.
/// <para>
/// URI forms:
/// <list type="bullet">
///   <item><c>controlbus:route?routeId=foo&amp;action=start</c> — act on a route.</item>
///   <item><c>controlbus:language:simple</c> — evaluate the message body as an expression.</item>
/// </list>
/// Parameters: <c>routeId</c> (or <c>current</c> = the sending route), <c>action</c>
/// (start|stop|suspend|resume|restart|status|stats|fail), <c>async</c> (default false), <c>restartDelay</c>
/// (ms, default 1000), <c>loggingLevel</c>. Registered out of the box under the <c>controlbus</c> scheme.
/// Per-context, like Camel: for cross-context control, send over <c>direct-vm</c>/<c>vm</c> to the target
/// context and run <c>controlbus:</c> there.
/// </para>
/// </summary>
public sealed class ControlBusComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "controlbus";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new ControlBusEndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // Command from the URI path: "route" (default), "language[:lang]", or "notify" / "event".
        var path = uri.Path ?? string.Empty;
        if (path.StartsWith("language", StringComparison.OrdinalIgnoreCase))
        {
            options.Command = ControlBusCommand.Language;
            var colon = path.IndexOf(':');
            options.Language = colon >= 0 && colon + 1 < path.Length ? path[(colon + 1)..] : "simple";
        }
        else if (path.StartsWith("notify", StringComparison.OrdinalIgnoreCase)
              || path.StartsWith("event", StringComparison.OrdinalIgnoreCase))
        {
            options.Command = ControlBusCommand.Notify;
        }
        else
        {
            options.Command = ControlBusCommand.Route;
        }

        options.Validate();
        return new ControlBusEndpoint(uri, this, options);
    }
}

// ── Options ──────────────────────────────────────────────────────────────────

/// <summary>Options for the control-bus endpoint.</summary>
public sealed class ControlBusEndpointOptions : EndpointOptions
{
    /// <summary>Command family (set from the URI path).</summary>
    public ControlBusCommand Command { get; set; } = ControlBusCommand.Route;

    /// <summary>Target route id. <c>current</c> resolves to the sending route. Optional for <c>stats</c>.</summary>
    public string? RouteId { get; set; }

    /// <summary>Action for the <c>route</c> command.</summary>
    public ControlBusAction? Action { get; set; }

    /// <summary>Perform the action asynchronously (fire-and-forget). Default: <c>false</c>.</summary>
    public bool Async { get; set; }

    /// <summary>Delay in ms between stop and start on <c>restart</c>. Default: 1000.</summary>
    public int RestartDelay { get; set; } = 1000;

    /// <summary>Logging level for the action. Default: <c>Info</c>.</summary>
    public string LoggingLevel { get; set; } = "Info";

    /// <summary>Expression language name for the <c>language</c> command (e.g. <c>simple</c>).</summary>
    public string? Language { get; set; }

    /// <summary>
    /// <c>notify</c> command only: comma-separated event-name filter (e.g. <c>RouteStarted,RouteStopped</c>).
    /// Empty = all events. Combine with <see cref="RouteId"/> to filter to one route.
    /// </summary>
    public string? Events { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        switch (Command)
        {
            case ControlBusCommand.Route:
                if (Action is null)
                    throw new ArgumentException("controlbus:route requires an 'action' (start|stop|suspend|resume|restart|status|stats|fail).");
                // routeId is required for everything except stats (stats without routeId = whole context).
                if (Action != ControlBusAction.Stats && string.IsNullOrWhiteSpace(RouteId))
                    throw new ArgumentException($"controlbus:route action '{Action}' requires a 'routeId' (or 'current').");
                break;
            case ControlBusCommand.Language:
                if (string.IsNullOrWhiteSpace(Language))
                    throw new ArgumentException("controlbus:language requires a language name, e.g. controlbus:language:simple.");
                break;
            case ControlBusCommand.Notify:
                // No required options — routeId and events are optional filters.
                break;
        }
    }
}

// ── Endpoint ─────────────────────────────────────────────────────────────────

/// <summary>Endpoint that runs a control-bus action when the route sends to it.</summary>
public sealed class ControlBusEndpoint : EndpointBase<ControlBusEndpointOptions>
{
    private readonly ControlBusEndpointOptions _options;

    internal ControlBusEndpoint(EndpointUri uri, ControlBusComponent component, ControlBusEndpointOptions options)
        : base(uri, component, options)
        => _options = options;

    internal ControlBusEndpointOptions ControlOptions => _options;

    /// <inheritdoc />
    public override IProducer CreateProducer()
        => _options.Command == ControlBusCommand.Notify
            ? throw new NotSupportedException(
                "controlbus:notify is a consumer (from) — it emits lifecycle events. Use From(\"controlbus:notify\").")
            : new ControlBusProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => _options.Command == ControlBusCommand.Notify
            ? new ControlBusEventConsumer(this, processor)
            : throw new NotSupportedException(
                "controlbus:route / controlbus:language are producer-only. Use .To(\"controlbus:...\") or .ControlBus(...); "
                + "to consume lifecycle events use From(\"controlbus:notify\").");
}

// ── Producer ─────────────────────────────────────────────────────────────────

/// <summary>Producer that dispatches the control-bus action against the route context.</summary>
public sealed class ControlBusProducer : IProducer
{
    private readonly ControlBusEndpoint _endpoint;

    internal ControlBusProducer(ControlBusEndpoint endpoint)
        => _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var context = (_endpoint.Component as ComponentBase)?.Context as RouteContext
            ?? throw new InvalidOperationException("Control Bus requires a RouteContext.");
        var o = _endpoint.ControlOptions;

        using var span = RouteTelemetryExtensions.StartTransportSpan(
            $"controlbus {o.Command}:{o.Action?.ToString() ?? o.Language}",
            ActivityKind.Client, "messaging.system", "controlbus",
            _endpoint.Uri.NormalizedKey, o.RouteId, o.Action?.ToString());

        if (o.Command == ControlBusCommand.Language)
        {
            // Evaluate a ${...} template against the exchange and return the result.
            // Boundary vs Camel: our engine is a template evaluator, not an OGNL that can invoke
            // context methods; lifecycle control is done via the 'route' command above.
            var template = exchange.In.Body?.ToString() ?? string.Empty;
            exchange.In.Body = ((EndpointOptions)o).ResolveOption(template, exchange);
            return;
        }

        var routeId = o.RouteId;
        if (string.Equals(routeId, "current", StringComparison.OrdinalIgnoreCase))
            routeId = exchange.RouteId;

        switch (o.Action)
        {
            case ControlBusAction.Status:
                exchange.In.Body = context.GetRoute(routeId!)?.Status.ToString() ?? "NotFound";
                return;
            case ControlBusAction.Stats:
                exchange.In.Body = BuildStats(context, routeId);
                return;
        }

        Func<Task> op = o.Action switch
        {
            ControlBusAction.Start => () => context.StartRoute(routeId!, ct),
            ControlBusAction.Stop or ControlBusAction.Suspend => () => context.StopRoute(routeId!, ct),
            ControlBusAction.Resume => () => context.ResumeRoute(routeId!, ct),
            ControlBusAction.Restart => async () =>
            {
                await context.StopRoute(routeId!).ConfigureAwait(false);
                if (o.RestartDelay > 0) await Task.Delay(o.RestartDelay).ConfigureAwait(false);
                await context.StartRoute(routeId!).ConfigureAwait(false);
            },
            ControlBusAction.Fail => async () =>
            {
                await context.StopRoute(routeId!).ConfigureAwait(false);
                if (context.GetRoute(routeId!) is { } r) r.Status = RouteStatus.Errored;
            },
            _ => throw new InvalidOperationException($"Unsupported control-bus action '{o.Action}'.")
        };

        // Stopping the current route synchronously would deadlock (this exchange is in-flight on it),
        // so self-targeting stop-like actions are always deferred, matching Camel's async guidance.
        var stopsRoute = o.Action is ControlBusAction.Stop or ControlBusAction.Suspend
            or ControlBusAction.Restart or ControlBusAction.Fail;
        var selfTarget = string.Equals(routeId, exchange.RouteId, StringComparison.OrdinalIgnoreCase);

        if (o.Async || (stopsRoute && selfTarget))
            _ = Task.Run(op, CancellationToken.None);
        else
            await op().ConfigureAwait(false);
    }

    private static string BuildStats(RouteContext context, string? routeId)
    {
        var routes = routeId is null
            ? context.Routes
            : context.GetRoute(routeId) is { } one ? new[] { one } : System.Array.Empty<CompiledRoute>();

        var sb = new StringBuilder();
        sb.Append("<routeStats>");
        foreach (var r in routes)
        {
            sb.Append("<route id=\"").Append(r.RouteId).Append("\" status=\"").Append(r.Status).Append('"');
            if (r.Endpoint is IEndpointStatistics s)
            {
                sb.Append(" messagesIn=\"").Append(s.MessagesIn).Append('"')
                  .Append(" messagesOut=\"").Append(s.MessagesOut).Append('"')
                  .Append(" errors=\"").Append(s.Errors).Append('"')
                  .Append(" throughputPerSecond=\"").Append(s.ThroughputPerSecond.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('"')
                  .Append(" health=\"").Append(s.HealthStatus).Append('"');
            }
            sb.Append("/>");
        }
        sb.Append("</routeStats>");
        return sb.ToString();
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default) => Task.CompletedTask;
}

// ── Event consumer (redb extension beyond Camel) ─────────────────────────────

/// <summary>
/// Consumer for <c>controlbus:notify</c>: emits route/context lifecycle events as exchanges into the route,
/// so you can react to them with the full EIP pipeline (filter, route, alert) instead of a callback.
/// Backed by <see cref="IRouteLifecycleListener"/> (redb's equivalent of Camel's EventNotifier SPI); Camel
/// has no such consumer, so this goes beyond parity. Optional filters: <c>routeId</c> (one route) and
/// <c>events</c> (comma-separated event names). Events are dispatched off the lifecycle-notification thread
/// so a slow reaction never stalls route start/stop.
/// </summary>
public sealed class ControlBusEventConsumer : IConsumer, IRouteLifecycleListener
{
    private readonly ControlBusEndpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly string? _routeFilter;
    private readonly HashSet<string>? _eventFilter;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private int _registered;
    private volatile bool _active;

    internal ControlBusEventConsumer(ControlBusEndpoint endpoint, IProcessor processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        var o = endpoint.ControlOptions;
        _routeFilter = string.IsNullOrWhiteSpace(o.RouteId) ? null : o.RouteId;
        _eventFilter = string.IsNullOrWhiteSpace(o.Events)
            ? null
            : new HashSet<string>(o.Events!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                                  StringComparer.OrdinalIgnoreCase);
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public IEndpoint Endpoint => _endpoint;

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        // Register with the context once (no RemoveLifecycleListener API); toggle emission with _active
        // so a restart does not double-register and a stop simply goes quiet.
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            (_endpoint.Component as ComponentBase)?.Context?.AddLifecycleListener(this);
        _active = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _active = false;
        return Task.CompletedTask;
    }

    // ── IRouteLifecycleListener → exchanges ──

    Task IRouteLifecycleListener.OnRouteStarted(string routeId, CancellationToken ct) { Emit("RouteStarted", routeId); return Task.CompletedTask; }
    Task IRouteLifecycleListener.OnRouteStopped(string routeId, CancellationToken ct) { Emit("RouteStopped", routeId); return Task.CompletedTask; }
    Task IRouteLifecycleListener.OnRouteSuspending(string routeId, CancellationToken ct) { Emit("RouteSuspending", routeId); return Task.CompletedTask; }
    Task IRouteLifecycleListener.OnRouteErrored(string routeId, Exception ex, CancellationToken ct)
    { Emit("RouteErrored", routeId, m => m.Headers[ControlBusHeaders.Error] = ex.Message); return Task.CompletedTask; }
    Task IRouteLifecycleListener.OnContextStarting(IRouteContext context, CancellationToken ct) { Emit("ContextStarting", null); return Task.CompletedTask; }
    Task IRouteLifecycleListener.OnContextStarted(IRouteContext context, CancellationToken ct) { Emit("ContextStarted", null); return Task.CompletedTask; }
    Task IRouteLifecycleListener.OnContextStopping(IRouteContext context, CancellationToken ct) { Emit("ContextStopping", null); return Task.CompletedTask; }
    Task IRouteLifecycleListener.OnContextStopped(IRouteContext context, CancellationToken ct) { Emit("ContextStopped", null); return Task.CompletedTask; }
    Task IRouteLifecycleListener.OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct)
    {
        Emit("ExchangeTimedOut", routeId, m =>
        {
            m.Headers[ControlBusHeaders.ExchangeId] = exchangeId;
            m.Headers[ControlBusHeaders.ElapsedMs] = elapsed.TotalMilliseconds;
        });
        return Task.CompletedTask;
    }

    private void Emit(string eventName, string? routeId, Action<IMessage>? decorate = null)
    {
        if (!_active) return;
        if (_eventFilter is not null && !_eventFilter.Contains(eventName)) return;
        // Route filter: when set, route-scoped events must match; context events (no routeId) are skipped.
        if (_routeFilter is not null && !string.Equals(routeId, _routeFilter, StringComparison.OrdinalIgnoreCase)) return;

        var message = new Message(eventName);
        message.Headers[ControlBusHeaders.Event] = eventName;
        if (routeId is not null) message.Headers[ControlBusHeaders.RouteId] = routeId;
        message.Headers[ControlBusHeaders.Timestamp] = DateTimeOffset.UtcNow;
        decorate?.Invoke(message);

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);

        // Off the lifecycle-notification thread: a slow reaction must not stall route start/stop,
        // and running the route pipeline reentrantly inside Start()/Stop() would be unsafe.
        _ = Task.Run(async () =>
        {
            try { await _processor.Process(exchange).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "controlbus:notify handler failed for event {Event}", eventName); }
            finally { await exchange.DisposeAsync().ConfigureAwait(false); }
        });
    }
}
