using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using redb.Route.Abstractions;

namespace redb.Route.Controllers;

/// <summary>
/// Dispatches gRPC requests to controller methods by method name.
/// <para>
/// The method name is read from <c>dispatch-method</c> header (a regular header
/// that flows through gRPC metadata without being filtered by producer/consumer).
/// Body is treated as JSON — single value or array of positional arguments.
/// </para>
/// <para>
/// Usage: the client (or middleware) sets <c>exchange.In.Headers["dispatch-method"] = "MethodName"</c>
/// before sending via <see cref="GrpcControllerDispatcher"/>. Qualified names like
/// <c>"Controller.Method"</c> are supported for multi-controller dispatch.
/// </para>
/// </summary>
public sealed class GrpcControllerDispatcher : IProcessor
{
    private readonly FrozenDictionary<string, MethodEntry> _methods;
    private readonly IRouteContext _context;

    /// <summary>Header key for the dispatch method name (flows through gRPC metadata unfiltered).</summary>
    internal const string MethodHeader = "dispatch-method";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <param name="context">Route context for controller instantiation.</param>
    /// <param name="controllerTypes">One or more controller types to register.</param>
    public GrpcControllerDispatcher(IRouteContext context, params Type[] controllerTypes)
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

            // gRPC body is byte[] — deserialize from JSON for positional binding
            var body = DeserializeBody(exchange.In.Body);
            var parameters = ParameterResolver.ResolvePositional(entry.Method, body, ct);
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

    /// <summary>
    /// Deserializes gRPC body. The consumer stores body as <c>byte[]</c>.
    /// If the JSON is an array, unpacks to <c>object[]</c> for positional binding.
    /// </summary>
    private static object? DeserializeBody(object? body)
    {
        if (body is null) return null;

        if (body is byte[] bytes)
        {
            if (bytes.Length == 0) return null;
            var element = JsonSerializer.Deserialize<JsonElement>(bytes, JsonOptions);
            return UnpackJsonElement(element);
        }

        // Already deserialized (e.g., in unit tests)
        return body;
    }

    private static object? UnpackJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var items = new object?[element.GetArrayLength()];
            for (var i = 0; i < items.Length; i++)
                items[i] = element[i];
            return items;
        }

        return element;
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

                // [GrpcMethod] pins the wire name so renaming the C# method is not a breaking change for
                // callers. Same role SoapOperationAttribute plays for SOAP operations.
                var name = method.GetCustomAttribute<Attributes.GrpcMethodAttribute>()?.Method ?? method.Name;

                // Qualified name: "Modules.GetAll" — always registered, last-write wins per controller
                map[$"{controllerName}.{name}"] = entry;

                // Short name: "GetAll" — only if no collision across controllers
                map.TryAdd(name, entry);
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

        // Serialize result to JSON bytes for gRPC response
        if (result is not byte[] and not string)
            result = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);

        exchange.Out.Body = result;
        exchange.Out.setHeader("status.code", 200);
    }

    private static void WriteError(IExchange exchange, int statusCode, string error, string message)
    {
        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = JsonSerializer.SerializeToUtf8Bytes(
            new ControllerErrorResponse
            {
                Error = error,
                Message = message,
                StatusCode = statusCode
            }, JsonOptions);
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
