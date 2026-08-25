using redb.Route.Abstractions;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Controllers.Extensions;

/// <summary>
/// DSL extension methods for dispatching exchanges to <see cref="RedbController"/> actions.
/// Provides five overloads of .RedbController() on <see cref="IRouteDefinition"/>.
/// </summary>
public static class ControllerRouteExtensions
{
    /// <summary>
    /// Dispatches to a controller action resolved from exchange headers (route.path, route.method).
    /// Full auto-dispatch: the dispatcher reads headers and matches against the registry.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="registry">Controller registry with registered actions.</param>
    public static IRouteDefinition RedbController(this IRouteDefinition route, ControllerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return route.Process((exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");
            var dispatcher = new ControllerDispatcherProcessor(registry, context);
            return dispatcher.Process(exchange, ct);
        });
    }

    /// <summary>
    /// Dispatches to a specific controller type. Method is resolved from exchange header (route.method)
    /// and path from (route.path), but only actions on <typeparamref name="TController"/> are considered.
    /// </summary>
    /// <typeparam name="TController">Controller type to dispatch to.</typeparam>
    /// <param name="route">Route definition.</param>
    public static IRouteDefinition RedbController<TController>(this IRouteDefinition route)
        where TController : RedbController
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(TController));
        return route.RedbController(registry);
    }

    /// <summary>
    /// Dispatches to a specific method on a specific controller type.
    /// Ignores route.method and route.path headers — directly invokes the named method.
    /// </summary>
    /// <typeparam name="TController">Controller type.</typeparam>
    /// <param name="route">Route definition.</param>
    /// <param name="methodName">Name of the method to invoke.</param>
    public static IRouteDefinition RedbController<TController>(this IRouteDefinition route, string methodName)
        where TController : RedbController
    {
        ArgumentNullException.ThrowIfNull(methodName);
        return route.Process(async (exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");

            var method = typeof(TController).GetMethod(methodName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (method is null)
                throw new InvalidOperationException($"Method '{methodName}' not found on {typeof(TController).Name}");

            var controller = (RedbController)Activator.CreateInstance(typeof(TController))!;
            controller.Context = context;
            controller.Exchange = exchange;

            var parameters = ParameterResolver.ResolveParameters(method, exchange, new Dictionary<string, string>());
            var result = method.Invoke(controller, parameters);

            if (result is Task task)
            {
                await task;
                result = GetTaskResult(task);
            }

            if (result is not null)
            {
                exchange.Out ??= exchange.In.Clone();
                exchange.Out.Body = result;
                exchange.Out.setHeader("status.code", 200);
            }
        });
    }

    /// <summary>
    /// Dispatches to a controller and method by string names.
    /// Resolves the controller type from the registry, then invokes the named method.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="registry">Controller registry.</param>
    /// <param name="controllerName">Controller name (without "Controller" suffix).</param>
    /// <param name="methodName">Method name to invoke.</param>
    public static IRouteDefinition RedbController(
        this IRouteDefinition route,
        ControllerRegistry registry,
        string controllerName,
        string methodName)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(controllerName);
        ArgumentNullException.ThrowIfNull(methodName);

        return route.Process(async (exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");

            var action = registry.Actions.FirstOrDefault(a =>
                a.ControllerType.Name.Replace("Controller", "")
                    .Equals(controllerName, StringComparison.OrdinalIgnoreCase)
                && a.Method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

            if (action is null)
                throw new InvalidOperationException(
                    $"No action found for controller '{controllerName}', method '{methodName}'");

            var controller = (RedbController)Activator.CreateInstance(action.ControllerType)!;
            controller.Context = context;
            controller.Exchange = exchange;

            var parameters = ParameterResolver.ResolveParameters(action.Method, exchange, new Dictionary<string, string>());
            var result = action.Method.Invoke(controller, parameters);

            if (result is Task task)
            {
                await task;
                result = GetTaskResult(task);
            }

            if (result is not null)
            {
                exchange.Out ??= exchange.In.Clone();
                exchange.Out.Body = result;
                exchange.Out.setHeader("status.code", 200);
            }
        });
    }

    /// <summary>
    /// Dispatches to a controller action using dynamic expressions for controller and method selection.
    /// The expressions are evaluated at runtime from exchange data.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="registry">Controller registry.</param>
    /// <param name="controllerExpr">Expression returning controller name from exchange.</param>
    /// <param name="methodExpr">Expression returning method name from exchange.</param>
    public static IRouteDefinition RedbController(
        this IRouteDefinition route,
        ControllerRegistry registry,
        Func<IExchange, string> controllerExpr,
        Func<IExchange, string> methodExpr)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(controllerExpr);
        ArgumentNullException.ThrowIfNull(methodExpr);

        return route.Process(async (exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");

            var controllerName = controllerExpr(exchange);
            var methodName = methodExpr(exchange);

            var action = registry.Actions.FirstOrDefault(a =>
                a.ControllerType.Name.Replace("Controller", "")
                    .Equals(controllerName, StringComparison.OrdinalIgnoreCase)
                && a.Method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

            if (action is null)
                throw new InvalidOperationException(
                    $"No action found for controller '{controllerName}', method '{methodName}'");

            var controller = (RedbController)Activator.CreateInstance(action.ControllerType)!;
            controller.Context = context;
            controller.Exchange = exchange;

            var parameters = ParameterResolver.ResolveParameters(action.Method, exchange, new Dictionary<string, string>());
            var result = action.Method.Invoke(controller, parameters);

            if (result is Task task)
            {
                await task;
                result = GetTaskResult(task);
            }

            if (result is not null)
            {
                exchange.Out ??= exchange.In.Clone();
                exchange.Out.Body = result;
                exchange.Out.setHeader("status.code", 200);
            }
        });
    }

    // ── SignalR controller dispatch ────────────────────────

    /// <summary>
    /// Dispatches SignalR hub invocations to methods on <typeparamref name="TController"/>.
    /// Method name is read from <c>redbSignalR.Method</c> header; arguments are resolved
    /// positionally from the exchange body.
    /// <para>No HTTP attributes required — all public instance methods are exposed.</para>
    /// </summary>
    /// <typeparam name="TController">Controller type whose methods become callable hub methods.</typeparam>
    /// <param name="route">Route definition.</param>
    public static IRouteDefinition RedbSignalRController<TController>(this IRouteDefinition route)
        where TController : RedbController
    {
        return route.Process((exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");
            var dispatcher = new SignalRControllerDispatcher(context, typeof(TController));
            return dispatcher.Process(exchange, ct);
        });
    }

    /// <summary>
    /// Dispatches SignalR hub invocations to methods across multiple controller types.
    /// Method name is read from <c>redbSignalR.Method</c> header. Use qualified format
    /// <c>"Controller.Method"</c> (e.g. <c>"Modules.GetAll"</c>) to disambiguate.
    /// Unqualified names resolve to first match.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="controllerTypes">Controller types to register.</param>
    public static IRouteDefinition RedbSignalRController(
        this IRouteDefinition route,
        params Type[] controllerTypes)
    {
        return route.Process((exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");
            var dispatcher = new SignalRControllerDispatcher(context, controllerTypes);
            return dispatcher.Process(exchange, ct);
        });
    }

    /// <summary>
    /// Dispatches SignalR hub invocations to controllers registered in an existing
    /// <see cref="ControllerRegistry"/>. Reuses the registry as a source of controller types;
    /// dispatch is by method name (not by HTTP method/path template).
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="registry">Controller registry with registered controllers.</param>
    public static IRouteDefinition RedbSignalRController(
        this IRouteDefinition route,
        ControllerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var types = registry.Actions.Select(a => a.ControllerType).Distinct().ToArray();
        return route.RedbSignalRController(types);
    }

    // ── SOAP controller dispatch ──────────────────────────

    /// <summary>
    /// Dispatches SOAP requests to methods on <typeparamref name="TController"/> by operation. The operation is
    /// read from <c>redbSoap.operation</c> (set by the SOAP consumer); the XML body binds to the method's
    /// <c>[FromBody]</c> / single complex parameter, and the return value is XML-serialized into the response.
    /// <para>No HTTP attributes required. Use <see cref="Attributes.SoapOperationAttribute"/> when the method
    /// name differs from the operation.</para>
    /// </summary>
    /// <typeparam name="TController">Controller whose methods become SOAP operations.</typeparam>
    /// <param name="route">Route definition (behind a <c>Soap.Listen(...)</c> consumer).</param>
    public static IRouteDefinition RedbSoapController<TController>(this IRouteDefinition route)
        where TController : RedbController
    {
        return route.Process((exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");
            var dispatcher = new SoapControllerDispatcher(context, typeof(TController));
            return dispatcher.Process(exchange, ct);
        });
    }

    /// <summary>
    /// Dispatches SOAP requests across multiple controller types by operation (<c>redbSoap.operation</c>). Use
    /// the qualified form <c>"Controller.Operation"</c> to disambiguate; unqualified names resolve to the first
    /// match.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="controllerTypes">Controller types to register.</param>
    public static IRouteDefinition RedbSoapController(
        this IRouteDefinition route,
        params Type[] controllerTypes)
    {
        return route.Process((exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");
            var dispatcher = new SoapControllerDispatcher(context, controllerTypes);
            return dispatcher.Process(exchange, ct);
        });
    }

    /// <summary>
    /// Dispatches SOAP requests to controllers registered in an existing <see cref="ControllerRegistry"/>,
    /// reusing it as a source of controller types; dispatch is by SOAP operation, not HTTP method/path.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="registry">Controller registry with registered controllers.</param>
    public static IRouteDefinition RedbSoapController(
        this IRouteDefinition route,
        ControllerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var types = registry.Actions.Select(a => a.ControllerType).Distinct().ToArray();
        return route.RedbSoapController(types);
    }

    // ── HTTP controller dispatch ──────────────────────────

    /// <summary>
    /// Dispatches HTTP requests to controller actions using REST conventions.
    /// Reads <c>redbHttp.Method</c> and <c>redbHttp.Path</c> headers set by the HTTP consumer,
    /// resolves actions from the <paramref name="registry"/>, and binds parameters from
    /// route templates (<c>redbHttp.RouteParam.*</c>), query strings (<c>redbHttp.QueryParam.*</c>),
    /// and request body (<c>byte[]</c>).
    /// <para>This is the HTTP-native dispatch — no manual header translation needed.</para>
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="registry">Controller registry with registered actions.</param>
    public static IRouteDefinition RedbHttpController(this IRouteDefinition route, ControllerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return route.Process((exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");
            var dispatcher = new HttpControllerDispatcher(registry, context);
            return dispatcher.Process(exchange, ct);
        });
    }

    /// <summary>
    /// Dispatches HTTP requests to a specific controller type using REST conventions.
    /// Reads <c>redbHttp.Method</c> and <c>redbHttp.Path</c> from the HTTP consumer.
    /// Only actions on <typeparamref name="TController"/> are considered.
    /// </summary>
    /// <typeparam name="TController">Controller type to dispatch to.</typeparam>
    /// <param name="route">Route definition.</param>
    public static IRouteDefinition RedbHttpController<TController>(this IRouteDefinition route)
        where TController : RedbController
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(TController));
        return route.RedbHttpController(registry);
    }

    // ── gRPC controller dispatch ──────────────────────────

    /// <summary>
    /// Dispatches gRPC requests to methods on <typeparamref name="TController"/>.
    /// Method name is read from <c>dispatch-method</c> header (flows through gRPC metadata
    /// unfiltered); body is deserialized from <c>byte[]</c> JSON for positional argument binding.
    /// <para>No HTTP attributes required — all public instance methods are exposed.</para>
    /// </summary>
    /// <typeparam name="TController">Controller type whose methods become callable via gRPC.</typeparam>
    /// <param name="route">Route definition.</param>
    public static IRouteDefinition RedbGrpcController<TController>(this IRouteDefinition route)
        where TController : RedbController
    {
        return route.Process((exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");
            var dispatcher = new GrpcControllerDispatcher(context, typeof(TController));
            return dispatcher.Process(exchange, ct);
        });
    }

    /// <summary>
    /// Dispatches gRPC requests to methods across multiple controller types.
    /// Method name is read from <c>dispatch-method</c> header. Use qualified format
    /// <c>"Controller.Method"</c> (e.g. <c>"Modules.GetAll"</c>) to disambiguate.
    /// Unqualified names resolve to first match.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="controllerTypes">Controller types to register.</param>
    public static IRouteDefinition RedbGrpcController(
        this IRouteDefinition route,
        params Type[] controllerTypes)
    {
        return route.Process((exchange, ct) =>
        {
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");
            var dispatcher = new GrpcControllerDispatcher(context, controllerTypes);
            return dispatcher.Process(exchange, ct);
        });
    }

    /// <summary>
    /// Dispatches gRPC requests to controllers registered in an existing
    /// <see cref="ControllerRegistry"/>. Reuses the registry as a source of controller types;
    /// dispatch is by method name (not by HTTP method/path template).
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="registry">Controller registry with registered controllers.</param>
    public static IRouteDefinition RedbGrpcController(
        this IRouteDefinition route,
        ControllerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var types = registry.Actions.Select(a => a.ControllerType).Distinct().ToArray();
        return route.RedbGrpcController(types);
    }

    private static object? GetTaskResult(Task task)
    {
        var type = task.GetType();
        if (!type.IsGenericType)
            return null;
        return type.GetProperty("Result")?.GetValue(task);
    }
}
