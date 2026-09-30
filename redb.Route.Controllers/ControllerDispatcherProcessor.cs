using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Controllers;

/// <summary>
/// Standard error response model for controller dispatch failures.
/// </summary>
public sealed class ControllerErrorResponse
{
    /// <summary>Error code or type.</summary>
    public string Error { get; init; } = "InternalError";

    /// <summary>Human-readable error message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>HTTP-style status code.</summary>
    public int StatusCode { get; init; } = 500;
}

/// <summary>
/// IProcessor that dispatches exchanges to <see cref="RedbController"/> actions.
/// Reads route.path and route.method from exchange headers, matches against <see cref="ControllerRegistry"/>,
/// resolves parameters, invokes the action, and writes the result to exchange.Out.
/// </summary>
public sealed class ControllerDispatcherProcessor : IProcessor
{
    private readonly ControllerRegistry _registry;
    private readonly IRouteContext _context;
    private readonly IReadOnlyList<IControllerActionFilter> _filters;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Emit non-ASCII (Cyrillic, emoji, diacritics) and ASCII punctuation like '"'
        // as-is in UTF-8 instead of escaping to \uXXXX. Safe for HTTP API responses;
        // only unsafe when embedding JSON inside HTML/JS, which dispatcher output never does.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Header key for the request path (e.g. "modules/123").</summary>
    public const string PathHeader = "route.path";

    /// <summary>Header key for the HTTP method (e.g. "GET", "POST").</summary>
    public const string MethodHeader = "route.method";

    /// <param name="registry">Controller registry with registered actions.</param>
    /// <param name="context">Route context for controller instantiation.</param>
    /// <param name="filters">
    /// Optional cross-cutting filters applied around every action invocation.
    /// Sorted ascending by <see cref="IControllerActionFilter.Order"/> at construction.
    /// </param>
    public ControllerDispatcherProcessor(
        ControllerRegistry registry,
        IRouteContext context,
        IEnumerable<IControllerActionFilter>? filters = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _filters = (filters ?? Array.Empty<IControllerActionFilter>())
            .OrderBy(f => f.Order)
            .ToArray();
        _logger = ControllerErrorReporting.CreateLogger<ControllerDispatcherProcessor>(context);
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var path = exchange.In.GetHeader<string>(PathHeader);
        var method = exchange.In.GetHeader<string>(MethodHeader);

        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(method))
        {
            WriteError(exchange, 400, "BadRequest", $"Missing required headers: {PathHeader} and/or {MethodHeader}");
            return;
        }

        var action = _registry.Resolve(method, path, out var routeParams);
        if (action is null)
        {
            WriteError(exchange, 404, "NotFound", $"No action matches {method} {path}");
            return;
        }

        var filterContext = _filters.Count > 0
            ? new ControllerActionContext(exchange, action, routeParams)
            : null;

        // BeforeAsync filters: ascending order. Errors are isolated per-filter.
        if (filterContext is not null)
        {
            for (var i = 0; i < _filters.Count; i++)
            {
                try { await _filters[i].BeforeAsync(filterContext, ct); }
                catch (Exception ex) { LogFilterFailure(ex, _filters[i], nameof(IControllerActionFilter.BeforeAsync)); }
            }
        }

        var sw = filterContext is not null ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            // The boundary between "the caller's error" and "our error" is the resolution step, as on the HTTP,
            // SignalR and gRPC dispatchers: a FormatException while binding a route or query value is a 400, the
            // same exception inside the action is a 500. The filters' AfterAsync still runs (finally below).
            object?[] parameters;
            try
            {
                parameters = ParameterResolver.ResolveParameters(action.Method, exchange, routeParams);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (filterContext is not null) filterContext.Exception = ex;
                WriteError(exchange, 400, ControllerErrorReporting.BadRequestCode,
                    ControllerErrorReporting.ReportBadRequest(_logger, ex, exchange, $"{method} {path}"));
                return;
            }
            if (filterContext is not null) filterContext.Arguments = parameters;

            var controller = (RedbController)Activator.CreateInstance(action.ControllerType)!;
            controller.Context = _context;
            controller.Exchange = exchange;

            // The action's own exception, one TargetInvocationException removed (see ActionInvoker).
            var result = await ActionInvoker.InvokeAsync(action.Method, controller, parameters);

            if (filterContext is not null) filterContext.Result = result;
            WriteResult(exchange, result);
        }
        catch (Exception ex)
        {
            if (filterContext is not null) filterContext.Exception = ex;
            WriteUnhandled(exchange, ex, path, method);
        }
        finally
        {
            if (filterContext is not null)
            {
                sw!.Stop();
                filterContext.Elapsed = sw.Elapsed;
                filterContext.StatusCode = exchange.Out?.GetHeader<int>("status.code") ?? 0;

                // AfterAsync filters: reverse order. Errors are isolated per-filter.
                for (var i = _filters.Count - 1; i >= 0; i--)
                {
                    try { await _filters[i].AfterAsync(filterContext, ct); }
                    catch (Exception ex) { LogFilterFailure(ex, _filters[i], nameof(IControllerActionFilter.AfterAsync)); }
                }
            }
        }
    }

    private static void WriteResult(IExchange exchange, object? result)
    {
        // An Out that already exists was written by the action (or a step before the dispatcher): that is the reply.
        // Only a clone the dispatcher makes here carries the request body, and only that one is cleared below.
        var createdHere = exchange.Out is null;
        exchange.Out ??= exchange.In.Clone();
        var defaultCode = result is null ? 204 : 200;

        if (result is not null)
        {
            exchange.Out.Body = result;
        }
        else if (createdHere)
        {
            // No result, no body: our own clone of the request is not the reply.
            exchange.Out.Body = null;
        }

        // Respect meta already set by the controller (e.g. facade Forward propagating an
        // inner OnException 5xx). Dispatcher only fills in defaults.
        if (!exchange.Out.Headers.ContainsKey("status.code"))
            exchange.Out.setHeader("status.code", defaultCode);
        if (result is not null && !exchange.Out.Headers.ContainsKey("Content-Type"))
            exchange.Out.setHeader("Content-Type", "application/json");
    }

    /// <summary>
    /// A filter threw. It never breaks dispatch, but it is logged: <see cref="IControllerActionFilter"/>
    /// documents exactly that, and a swallowed throw made an audit filter that fails on every request
    /// indistinguishable from one that works.
    /// </summary>
    private void LogFilterFailure(Exception exception, IControllerActionFilter filter, string stage)
        => _logger?.LogError(exception, "Controller action filter {Filter} threw in {Stage}; dispatch continues", filter.GetType().FullName, stage);

    /// <summary>An action failed: generic message out, the exception to the log (see <see cref="ControllerErrorReporting"/>).</summary>
    private void WriteUnhandled(IExchange exchange, Exception exception, string? path, string? method)
        => WriteError(exchange, 500, ControllerErrorReporting.ErrorCode,
            ControllerErrorReporting.Report(_logger, exception, exchange, $"{method} {path}"));

    private static void WriteError(IExchange exchange, int statusCode, string error, string message)
    {
        var errorResponse = new ControllerErrorResponse
        {
            Error = error,
            Message = message,
            StatusCode = statusCode
        };

        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = errorResponse;
        // Errors are authoritative: overwrite whatever a controller may have set before throwing.
        exchange.Out.setHeader("status.code", statusCode);
        exchange.Out.setHeader("Content-Type", "application/json");
    }
}
