using System.Collections.Frozen;
using System.Reflection;
using redb.Route.Abstractions;

namespace redb.Route.Controllers;

/// <summary>
/// Dispatches SignalR hub invocations to controller methods by name.
/// Reads the method name from <c>redbSignalR.Method</c> header and resolves positional arguments
/// from the exchange body (as set by <c>RedbBridgeHub.Invoke</c>).
/// <para>
/// Single controller: method name is looked up directly (e.g. <c>"GetAll"</c>).
/// Multiple controllers: qualified format <c>"Controller.Method"</c> (e.g. <c>"Modules.GetAll"</c>)
/// is preferred; unqualified names resolve to first match.
/// </para>
/// </summary>
public sealed class SignalRControllerDispatcher : IProcessor
{
    private readonly FrozenDictionary<string, MethodEntry> _methods;
    private readonly IRouteContext _context;

    /// <summary>Header key for the SignalR method name (matches SignalRHeaders.Method).</summary>
    internal const string MethodHeader = "redbSignalR.Method";

    /// <param name="context">Route context for controller instantiation.</param>
    /// <param name="controllerTypes">One or more controller types to register.</param>
    public SignalRControllerDispatcher(IRouteContext context, params Type[] controllerTypes)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (controllerTypes is null || controllerTypes.Length == 0)
            throw new ArgumentException("At least one controller type is required.", nameof(controllerTypes));

        _methods = BuildMethodMap(controllerTypes);
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var methodName = exchange.In.GetHeader<string>(MethodHeader);
        if (string.IsNullOrEmpty(methodName))
        {
            WriteError(exchange, 400, "BadRequest", $"Missing required header: {MethodHeader}");
            return;
        }

        if (!_methods.TryGetValue(methodName, out var entry))
        {
            WriteError(exchange, 404, "NotFound", $"Method '{methodName}' not found on registered controllers");
            return;
        }

        try
        {
            var controller = (RedbController)Activator.CreateInstance(entry.ControllerType)!;
            controller.Context = _context;
            controller.Exchange = exchange;

            var parameters = ParameterResolver.ResolvePositional(entry.Method, exchange.In.Body, ct);
            var result = entry.Method.Invoke(controller, parameters);

            if (result is Task task)
            {
                await task;
                result = GetTaskResult(task);
            }

            WriteResult(exchange, result);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            WriteError(exchange, 500, "InternalError", inner.Message);
        }
    }

    private static FrozenDictionary<string, MethodEntry> BuildMethodMap(Type[] controllerTypes)
    {
        var map = new Dictionary<string, MethodEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in controllerTypes)
        {
            if (!type.IsSubclassOf(typeof(RedbController)))
                throw new ArgumentException($"Type '{type.Name}' does not inherit from RedbController.");

            var controllerName = type.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? type.Name[..^10]
                : type.Name;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var entry = new MethodEntry(type, method);

                // Qualified name: "Modules.GetAll" — always registered, last-write wins per controller
                map[$"{controllerName}.{method.Name}"] = entry;

                // Short name: "GetAll" — only if no collision across controllers
                map.TryAdd(method.Name, entry);
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteResult(IExchange exchange, object? result)
    {
        exchange.Out ??= exchange.In.Clone();
        if (result is null)
        {
            exchange.Out.setHeader("status.code", 204);
            return;
        }

        exchange.Out.Body = result;
        exchange.Out.setHeader("status.code", 200);
    }

    private static void WriteError(IExchange exchange, int statusCode, string error, string message)
    {
        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = new ControllerErrorResponse
        {
            Error = error,
            Message = message,
            StatusCode = statusCode
        };
        exchange.Out.setHeader("status.code", statusCode);
    }

    private static object? GetTaskResult(Task task)
    {
        var type = task.GetType();
        if (!type.IsGenericType) return null;
        return type.GetProperty("Result")?.GetValue(task);
    }

    internal sealed record MethodEntry(Type ControllerType, MethodInfo Method);
}
