using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
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
                catch { /* Filters must not break dispatch. */ }
            }
        }

        var sw = filterContext is not null ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            var controller = (RedbController)Activator.CreateInstance(action.ControllerType)!;
            controller.Context = _context;
            controller.Exchange = exchange;

            var parameters = ParameterResolver.ResolveParameters(action.Method, exchange, routeParams);
            if (filterContext is not null) filterContext.Arguments = parameters;

            var result = action.Method.Invoke(controller, parameters);

            // Await if the method returns a Task
            if (result is Task task)
            {
                await task;
                result = GetTaskResult(task);
            }

            if (filterContext is not null) filterContext.Result = result;
            WriteResult(exchange, result);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Sync controller methods surface user exceptions wrapped in TIE via MethodInfo.Invoke.
            // Async methods return a faulted Task; `await` re-throws the original exception (no TIE).
            // Only unwrap TIE — never blindly deref .InnerException on arbitrary exceptions.
            if (filterContext is not null) filterContext.Exception = tie.InnerException;
            WriteError(exchange, 500, "InternalError", tie.InnerException.Message);
        }
        catch (Exception ex)
        {
            if (filterContext is not null) filterContext.Exception = ex;
            WriteError(exchange, 500, "InternalError", ex.Message);
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
                    catch { /* Filters must not break dispatch. */ }
                }
            }
        }
    }

    private static void WriteResult(IExchange exchange, object? result)
    {
        exchange.Out ??= exchange.In.Clone();
        var defaultCode = result is null ? 204 : 200;

        if (result is not null)
        {
            exchange.Out.Body = result;
        }

        // Respect meta already set by the controller (e.g. facade Forward propagating an
        // inner OnException 5xx). Dispatcher only fills in defaults.
        if (!exchange.Out.Headers.ContainsKey("status.code"))
            exchange.Out.setHeader("status.code", defaultCode);
        if (result is not null && !exchange.Out.Headers.ContainsKey("Content-Type"))
            exchange.Out.setHeader("Content-Type", "application/json");
    }

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

    private static object? GetTaskResult(Task task)
    {
        var type = task.GetType();
        if (!type.IsGenericType)
            return null;

        // Task<T> — extract .Result
        return type.GetProperty("Result")?.GetValue(task);
    }
}
