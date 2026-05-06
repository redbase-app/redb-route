using redb.Route.Abstractions;

namespace redb.Route.Controllers;

/// <summary>
/// Per-action invocation context passed to <see cref="IControllerActionFilter"/>s.
/// All mutable fields are populated by <see cref="ControllerDispatcherProcessor"/>
/// before filters are invoked.
/// </summary>
public sealed class ControllerActionContext
{
    /// <summary>The current message exchange.</summary>
    public IExchange Exchange { get; }

    /// <summary>Resolved controller action descriptor.</summary>
    public ControllerAction Action { get; }

    /// <summary>Route parameters extracted from the path.</summary>
    public IReadOnlyDictionary<string, string> RouteParams { get; }

    /// <summary>Resolved positional argument values for the action method.</summary>
    public object?[] Arguments { get; internal set; } = Array.Empty<object?>();

    /// <summary>Action result (set after invocation, before <see cref="IControllerActionFilter.AfterAsync"/>).</summary>
    public object? Result { get; internal set; }

    /// <summary>Exception thrown by the action (if any).</summary>
    public Exception? Exception { get; internal set; }

    /// <summary>HTTP-style status code written to the response.</summary>
    public int StatusCode { get; internal set; }

    /// <summary>Wall-clock duration of the action invocation (excluding filter Before phase).</summary>
    public TimeSpan Elapsed { get; internal set; }

    /// <summary>Free-form items bag for filter-to-filter communication within a single invocation.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public ControllerActionContext(
        IExchange exchange,
        ControllerAction action,
        IReadOnlyDictionary<string, string> routeParams)
    {
        Exchange = exchange;
        Action = action;
        RouteParams = routeParams;
    }
}

/// <summary>
/// Optional filter SPI invoked around every controller action by
/// <see cref="ControllerDispatcherProcessor"/>. Filters are resolved once at
/// dispatcher construction time and ordered by <see cref="Order"/> ascending.
/// <para>
/// <see cref="BeforeAsync"/> runs in ascending order, <see cref="AfterAsync"/>
/// runs in reverse (descending) order — classic onion model.
/// </para>
/// <para>
/// Filters MUST be tolerant: throwing from a filter is logged but does not propagate
/// to the action result. Use this for cross-cutting concerns like auditing, metrics,
/// correlation IDs.
/// </para>
/// </summary>
public interface IControllerActionFilter
{
    /// <summary>Lower values run first in <see cref="BeforeAsync"/>, last in <see cref="AfterAsync"/>.</summary>
    int Order { get; }

    /// <summary>Invoked before the action method is called.</summary>
    Task BeforeAsync(ControllerActionContext context, CancellationToken ct);

    /// <summary>
    /// Invoked after the action method completes (whether success or failure).
    /// Inspect <see cref="ControllerActionContext.Exception"/> to detect failures.
    /// </summary>
    Task AfterAsync(ControllerActionContext context, CancellationToken ct);
}
