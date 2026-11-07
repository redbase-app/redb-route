using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Demo;

// ═══════════════════════════════════════════════════════════════════════════════
//   ProducerTemplateStarter — starts the shared ProducerTemplate after context start
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Starts the shared <see cref="ProducerTemplate"/> once the context is up so the
/// LLM agent engine can dispatch tool routes through it. Without this, the first
/// tool call from <c>/api/llm/shell</c> would throw "ProducerTemplate is not started".
/// </summary>
internal sealed class ProducerTemplateStarter : IRouteLifecycleListener
{
    private readonly ProducerTemplate _template;
    private readonly ILogger? _log;

    public ProducerTemplateStarter(ProducerTemplate template, ILogger? log)
    {
        _template = template;
        _log = log;
    }

    public Task OnContextStarting(IRouteContext context, CancellationToken ct) => Task.CompletedTask;

    public Task OnContextStarted(IRouteContext context, CancellationToken ct)
    {
        if (!_template.IsStarted)
        {
            _template.Start();
            _log?.LogInformation("[LLM] ProducerTemplate started (tool dispatch ready)");
        }
        return Task.CompletedTask;
    }

    public Task OnContextStopping(IRouteContext context, CancellationToken ct)
    {
        if (_template.IsStarted) _template.Stop();
        return Task.CompletedTask;
    }

    public Task OnContextStopped(IRouteContext context, CancellationToken ct) => Task.CompletedTask;
    public Task OnRouteStarted(string routeId, CancellationToken ct) => Task.CompletedTask;
    public Task OnRouteStopped(string routeId, CancellationToken ct) => Task.CompletedTask;
    public Task OnRouteSuspending(string routeId, CancellationToken ct) => Task.CompletedTask;
    public Task OnRouteErrored(string routeId, Exception ex, CancellationToken ct) => Task.CompletedTask;
    public Task OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct) => Task.CompletedTask;
}

// ═══════════════════════════════════════════════════════════════════════════════
//   DemoLifecycleListener — logs all context & route lifecycle events
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Logs every lifecycle event to demonstrate <see cref="IRouteLifecycleListener"/>.
/// Register via <c>context.AddLifecycleListener(...)</c> in InitRoute.
/// </summary>
public class DemoLifecycleListener : IRouteLifecycleListener
{
    private readonly ILogger? _log;

    public DemoLifecycleListener(ILogger? logger) => _log = logger;

    // ── Context-level events ──

    public Task OnContextStarting(IRouteContext context, CancellationToken ct)
    {
        _log?.LogInformation("[LIFECYCLE] ▶ Context STARTING");
        return Task.CompletedTask;
    }

    public Task OnContextStarted(IRouteContext context, CancellationToken ct)
    {
        _log?.LogInformation("[LIFECYCLE] ✔ Context STARTED");
        return Task.CompletedTask;
    }

    public Task OnContextStopping(IRouteContext context, CancellationToken ct)
    {
        _log?.LogInformation("[LIFECYCLE] ■ Context STOPPING");
        return Task.CompletedTask;
    }

    public Task OnContextStopped(IRouteContext context, CancellationToken ct)
    {
        _log?.LogInformation("[LIFECYCLE] ✖ Context STOPPED");
        return Task.CompletedTask;
    }

    // ── Route-level events ──

    public Task OnRouteStarted(string routeId, CancellationToken ct)
    {
        _log?.LogInformation("[LIFECYCLE]   ▶ Route '{RouteId}' STARTED", routeId);
        return Task.CompletedTask;
    }

    public Task OnRouteStopped(string routeId, CancellationToken ct)
    {
        _log?.LogInformation("[LIFECYCLE]   ■ Route '{RouteId}' STOPPED", routeId);
        return Task.CompletedTask;
    }

    public Task OnRouteSuspending(string routeId, CancellationToken ct)
    {
        _log?.LogInformation("[LIFECYCLE]   ⏸ Route '{RouteId}' SUSPENDING", routeId);
        return Task.CompletedTask;
    }

    public Task OnRouteErrored(string routeId, Exception ex, CancellationToken ct)
    {
        _log?.LogWarning(ex, "[LIFECYCLE]   ✖ Route '{RouteId}' ERRORED", routeId);
        return Task.CompletedTask;
    }

    public Task OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct)
    {
        _log?.LogWarning("[LIFECYCLE]   ⏱ Route '{RouteId}' exchange {ExchangeId} timed out after {Elapsed}",
            routeId, exchangeId, elapsed);
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//   DemoRoutePolicy — logs per-route lifecycle and per-exchange events
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Demonstrates <see cref="RoutePolicySupport"/> by logging every policy hook.
/// Attach to a route via <c>.RoutePolicy(new DemoRoutePolicy(logger))</c>.
/// </summary>
public class DemoRoutePolicy : RoutePolicySupport
{
    private readonly ILogger? _log;

    public DemoRoutePolicy(ILogger? logger) => _log = logger;

    public override Task OnInit(IRouteContext context, CompiledRoute route, CancellationToken ct)
    {
        base.OnInit(context, route, ct);
        _log?.LogInformation("[POLICY] Route '{RouteId}' — OnInit (compiled, not yet started)", RouteId);
        return Task.CompletedTask;
    }

    public override Task OnStart(IRouteContext context, CompiledRoute route, CancellationToken ct)
    {
        _log?.LogInformation("[POLICY] Route '{RouteId}' — OnStart", RouteId);
        return Task.CompletedTask;
    }

    public override Task OnStop(IRouteContext context, CompiledRoute route, CancellationToken ct)
    {
        _log?.LogInformation("[POLICY] Route '{RouteId}' — OnStop", RouteId);
        return Task.CompletedTask;
    }

    public override Task OnRemove(IRouteContext context, CompiledRoute route, CancellationToken ct)
    {
        _log?.LogInformation("[POLICY] Route '{RouteId}' — OnRemove", RouteId);
        return Task.CompletedTask;
    }

    public override Task OnSuspend(IRouteContext context, CompiledRoute route, CancellationToken ct)
    {
        _log?.LogInformation("[POLICY] Route '{RouteId}' — OnSuspend", RouteId);
        return Task.CompletedTask;
    }

    public override Task OnResume(IRouteContext context, CompiledRoute route, CancellationToken ct)
    {
        _log?.LogInformation("[POLICY] Route '{RouteId}' — OnResume", RouteId);
        return Task.CompletedTask;
    }

    public override Task OnExchangeBegin(IRouteContext context, IExchange exchange, CancellationToken ct)
    {
        _log?.LogDebug("[POLICY] Route '{RouteId}' — ExchangeBegin {ExchangeId}",
            RouteId, exchange.ExchangeId);
        return Task.CompletedTask;
    }

    public override Task OnExchangeDone(IRouteContext context, IExchange exchange, CancellationToken ct)
    {
        _log?.LogDebug("[POLICY] Route '{RouteId}' — ExchangeDone {ExchangeId} (failed={Failed})",
            RouteId, exchange.ExchangeId, exchange.Exception != null);
        return Task.CompletedTask;
    }
}
