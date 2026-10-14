using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Core;

/// <summary>
/// The error handling of one route, as in Apache Camel, from the inside out:
/// <list type="number">
/// <item>the route's own <c>OnException</c> blocks (hoisted from its body);</item>
/// <item>the <c>OnException</c> blocks the route shares with other routes: those of its builder — that builder's only
/// (Camel's RouteBuilder scope) — then those of its route configurations, which are added after the builder's and so
/// are used instead of a builder's handler of the same exception type;</item>
/// <item>the context's error handling (<see cref="ContextErrorLayerProcessor"/>): <c>AddLocalExceptionHandler</c> of the
/// route, <c>AddGlobalExceptionHandler</c>, <see cref="IRouteContext.ErrorHandler"/> — for a failure no <c>OnException</c>
/// took, as Camel's context error handler.</item>
/// </list>
/// <para>
/// Layers 1 and 2 choose together, as Camel's exception policy does: the handler whose exception type is nearest to
/// the failure wins wherever it is declared, and the route's own wins a tie (<see cref="OnExceptionProcessor"/>).
/// So a route's <c>OnException&lt;Exception&gt;</c> does not hide a shared <c>OnException&lt;IOException&gt;</c>.
/// </para>
/// <para>
/// An ordinary route is wrapped in two parts, because the route policy and the processing timeout sit between them:
/// <see cref="WrapRoute"/> (layer 1, inside the route body) and <see cref="WrapOuter"/> (layers 2 and 3, around the
/// timeout). A body that runs after its caller has returned — <c>.Threads()</c> for an in-only exchange — is no longer
/// inside those wrappers, so it is wrapped by the whole scope (<see cref="WrapDetached"/>): a failure there gets the same
/// handlers with their whole configuration (<c>OnWhen</c>, redeliveries, <c>Handled</c>, <c>UseOriginalBody</c>...).
/// </para>
/// <para>
/// Each handler body is compiled once, where it always was: the route's inside the route's compile frame, the shared
/// ones after it. The wrappers are therefore completed only in <see cref="WrapOuter"/>, by which time every handler
/// exists; a detached wrapper refuses to run before that (the route has not finished compiling).
/// </para>
/// </summary>
internal sealed class RouteErrorScope
{
    private readonly RouteContext _context;
    private readonly IReadOnlyList<OnExceptionDefinition> _sharedHandlers;
    private readonly List<OnExceptionDefinition> _routeHandlers = [];
    private readonly Dictionary<OnExceptionDefinition, IProcessor> _bodies = new(ReferenceEqualityComparer.Instance);
    private readonly List<DetachedBody> _detached = [];
    private readonly ILogger? _logger;
    private OnExceptionProcessor? _routeLayer;
    private bool _sealed;

    /// <param name="context">The context the route compiles in.</param>
    /// <param name="routeId">The route this scope belongs to.</param>
    /// <param name="builderHandlers">The <c>OnException</c> blocks of the route's builder.</param>
    /// <param name="configurationHandlers">The <c>OnException</c> blocks of the route configurations of the route.</param>
    /// <param name="logger">Logger of the handlers.</param>
    internal RouteErrorScope(RouteContext context, string routeId, IReadOnlyList<OnExceptionDefinition> builderHandlers,
        IReadOnlyList<OnExceptionDefinition> configurationHandlers, ILogger? logger)
    {
        _context = context;
        RouteId = routeId;
        _sharedHandlers = [.. builderHandlers, .. configurationHandlers];
        _logger = logger;
    }

    /// <summary>The route this scope belongs to.</summary>
    internal string RouteId { get; }

    /// <summary>Takes the route's own <c>OnException</c> blocks, in declaration order.</summary>
    internal void AddRouteHandlers(IEnumerable<OnExceptionDefinition> handlers) => _routeHandlers.AddRange(handlers);

    /// <summary>Layer 1: the route's own <c>OnException</c> blocks around <paramref name="body"/>.</summary>
    internal IProcessor WrapRoute(IProcessor body)
    {
        _routeLayer = Layer(body, _routeHandlers);
        return _routeLayer ?? body;
    }

    /// <summary>
    /// Layers 2 and 3 around <paramref name="body"/>: the shared <c>OnException</c> blocks, then the context's error
    /// handling. Links the route's own layer to the shared one and completes the detached wrappers of this route.
    /// </summary>
    internal IProcessor WrapOuter(IProcessor body)
    {
        var shared = Layer(body, _sharedHandlers);
        if (_routeLayer is not null)
            _routeLayer.Outer = shared;

        foreach (var detached in _detached)
        {
            var route = Layer(detached.Body, _routeHandlers);
            var outer = Layer(route ?? detached.Body, _sharedHandlers);
            if (route is not null)
                route.Outer = outer;
            detached.Target = Context(outer ?? route ?? detached.Body);
        }
        _sealed = true;
        return Context(shared ?? body);
    }

    /// <summary>
    /// The whole scope around a body that runs after its caller has returned. The wrapper is completed by
    /// <see cref="WrapOuter"/>, when every handler body of the route is compiled.
    /// </summary>
    internal IProcessor WrapDetached(IProcessor body)
    {
        if (_sealed)
            throw new InvalidOperationException($"The error scope of route '{RouteId}' is already complete.");
        var detached = new DetachedBody(body, RouteId);
        _detached.Add(detached);
        return detached;
    }

    /// <summary>
    /// Whether an <c>OnException</c> of this route, of its builder or of its route configurations covers
    /// <paramref name="exceptionType"/>.
    /// </summary>
    internal bool Handles(Type exceptionType)
        => _routeHandlers.Concat(_sharedHandlers)
            .Any(h => h.ExceptionTypes.Any(t => t.IsAssignableFrom(exceptionType)));

    private IProcessor Context(IProcessor body) => new ContextErrorLayerProcessor(body, _context, RouteId);

    /// <summary>One processor for all of <paramref name="handlers"/> around <paramref name="body"/>; none when there are no handlers.</summary>
    private OnExceptionProcessor? Layer(IProcessor body, IReadOnlyList<OnExceptionDefinition> handlers)
    {
        if (handlers.Count == 0)
            return null;
        var layer = new OnExceptionProcessor(body, _logger);
        foreach (var handler in handlers)
            handler.AddTo(layer, BodyOf(handler));
        return layer;
    }

    private IProcessor BodyOf(OnExceptionDefinition handler)
    {
        if (!_bodies.TryGetValue(handler, out var body))
        {
            body = _context.CompileHandlerBody(handler.Outputs);
            _bodies[handler] = body;
        }
        return body;
    }

    /// <summary>A detached body whose error scope is completed when the route's compilation ends.</summary>
    private sealed class DetachedBody(IProcessor body, string routeId) : IProcessor
    {
        internal IProcessor Body { get; } = body;

        internal IProcessor? Target { get; set; }

        public Task Process(IExchange exchange, CancellationToken ct = default)
            => (Target ?? throw new InvalidOperationException(
                    $"Route '{routeId}' has not finished compiling: its error scope is not complete."))
                .Process(exchange, ct);
    }
}