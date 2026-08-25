using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using redb.Route.Abstractions;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Controllers;

/// <summary>
/// Dispatches SOAP requests to controller methods by operation name, making SOAP a first-class controller
/// transport alongside HTTP, SignalR and gRPC. The operation is read from <c>redbSoap.operation</c> (the local
/// name of the <c>&lt;soap:Body&gt;</c> root element, which the SOAP consumer sets); it maps to a method whose
/// name — or <see cref="SoapOperationAttribute"/> — matches. The request body is XML and binds to the method's
/// <c>[FromBody]</c> (or single complex) parameter via <see cref="XmlSerializer"/>; the return value is
/// XML-serialized onto <see cref="IExchange.Out"/>, which the SOAP consumer wraps in a response envelope.
/// A missing operation, an unknown operation, or an invocation exception throws — the SOAP consumer turns that
/// into a <c>soap:Fault</c>.
/// <para>
/// Use it behind a consumer in the default <c>Payload</c> data format: the dispatcher owns per-operation typing
/// and the inner <c>&lt;soap:Body&gt;</c> XML, which is what <c>Payload</c> delivers. It is not meant to be
/// combined with <c>Message</c> (which hands over the whole envelope and sends the reply without wrapping) or
/// <c>Pojo</c> (which fixes one request type on the endpoint and so collapses multi-operation dispatch).
/// </para>
/// </summary>
public sealed class SoapControllerDispatcher : IProcessor
{
    /// <summary>Header the SOAP consumer sets to the request operation (Body root element local name).</summary>
    public const string OperationHeader = "redbSoap.operation";

    private static readonly ConcurrentDictionary<Type, XmlSerializer> Serializers = new();
    private readonly FrozenDictionary<string, MethodEntry> _operations;
    private readonly IRouteContext _context;

    /// <param name="context">Route context for controller instantiation.</param>
    /// <param name="controllerTypes">One or more controller types to register.</param>
    public SoapControllerDispatcher(IRouteContext context, params Type[] controllerTypes)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (controllerTypes is null || controllerTypes.Length == 0)
            throw new ArgumentException("At least one controller type is required.", nameof(controllerTypes));
        _operations = BuildOperationMap(controllerTypes);
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var operation = exchange.In.GetHeader<string>(OperationHeader);
        if (string.IsNullOrEmpty(operation))
            throw new InvalidOperationException(
                $"SOAP dispatch requires the '{OperationHeader}' header (set by the SOAP consumer).");

        if (!_operations.TryGetValue(operation, out var entry))
            throw new InvalidOperationException($"No SOAP operation '{operation}' on the registered controllers.");

        var controller = (RedbController)Activator.CreateInstance(entry.ControllerType)!;
        controller.Context = _context;
        controller.Exchange = exchange;

        var args = ResolveArgs(entry.Method, exchange, ct);

        object? result;
        try { result = entry.Method.Invoke(controller, args); }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }

        // Await Task / Task<T> and ValueTask / ValueTask<T> (the latter is a struct, so `is Task` misses it).
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            result = GetTaskResult(task);
        }
        else if (result is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            result = null;
        }
        else if (result?.GetType() is { IsGenericType: true } rt && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = (Task)rt.GetMethod("AsTask")!.Invoke(result, null)!;
            await asTask.ConfigureAwait(false);
            result = GetTaskResult(asTask);
        }

        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = SerializeResult(result);
        exchange.Out.ContentType = "text/xml";
    }

    private static object?[] ResolveArgs(MethodInfo method, IExchange exchange, CancellationToken ct)
    {
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(CancellationToken)) { values[i] = ct; continue; }

            // Header binding shares the JSON dispatchers' value conversion.
            if (p.GetCustomAttribute<FromHeaderAttribute>() is { } h)
            {
                var raw = exchange.In.getHeader(h.Name);
                values[i] = raw is not null ? ParameterResolver.ConvertValue(raw, p.ParameterType)
                    : (p.HasDefaultValue ? p.DefaultValue : ParameterResolver.ConvertValue(null, p.ParameterType));
                continue;
            }

            // The SOAP Body is XML: [FromBody] or a single complex parameter deserializes from it.
            if (p.GetCustomAttribute<FromBodyAttribute>() is not null || !IsSimpleType(p.ParameterType))
            {
                values[i] = DeserializeXml(exchange.In.Body, p.ParameterType);
                continue;
            }

            if (p.HasDefaultValue) { values[i] = p.DefaultValue; continue; }

            // A required simple parameter with no binding cannot come from the XML body. Fail with a readable
            // message (which becomes the soap:Fault reason) instead of a cryptic reflection ArgumentException.
            var t = p.ParameterType;
            if (t.IsValueType && Nullable.GetUnderlyingType(t) is null)
                throw new InvalidOperationException(
                    $"SOAP operation cannot bind parameter '{p.Name}' of type '{t.Name}': annotate it with " +
                    "[FromHeader], or give it a default value. The SOAP body binds to the [FromBody] / complex parameter.");
            values[i] = null;
        }
        return values;
    }

    private static object? DeserializeXml(object? body, Type type)
    {
        if (body is null) return null;
        if (type.IsInstanceOfType(body)) return body;
        var xml = body switch
        {
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b),
            _ => body.ToString() ?? string.Empty,
        };
        if (string.IsNullOrWhiteSpace(xml)) return null;
        if (type == typeof(string)) return xml;
        using var sr = new StringReader(xml);
        return Serializer(type).Deserialize(sr);
    }

    private static object? SerializeResult(object? result)
    {
        if (result is null) return string.Empty;
        if (result is string s) return s;
        if (result is byte[]) return result;

        var ns = new XmlSerializerNamespaces();
        ns.Add(string.Empty, string.Empty);                 // drop xsi/xsd noise, keep the type's namespace
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false };
        using var sw = new StringWriter();
        using (var xw = XmlWriter.Create(sw, settings))
            Serializer(result.GetType()).Serialize(xw, result, ns);
        return sw.ToString();
    }

    private static XmlSerializer Serializer(Type t) => Serializers.GetOrAdd(t, static x => new XmlSerializer(x));

    private static FrozenDictionary<string, MethodEntry> BuildOperationMap(Type[] controllerTypes)
    {
        // Case-SENSITIVE: the operation is the Body root element's local name, and XML names are case-sensitive.
        var map = new Dictionary<string, MethodEntry>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);   // op -> "Controller.Method" that claimed it
        foreach (var type in controllerTypes)
        {
            if (!type.IsSubclassOf(typeof(RedbController)))
                throw new ArgumentException($"Type '{type.Name}' does not inherit from RedbController.");

            var controllerName = type.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? type.Name[..^10]
                : type.Name;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var op = method.GetCustomAttribute<SoapOperationAttribute>()?.Operation ?? method.Name;

                // Dispatch is by the unqualified operation only (the qualified "Controller.op" form is never on
                // the wire), so a duplicate is an unresolvable ambiguity: fail fast rather than silently run the
                // first-registered handler for another controller's request.
                if (owners.TryGetValue(op, out var owner))
                    throw new InvalidOperationException(
                        $"Ambiguous SOAP operation '{op}': claimed by both {owner} and {controllerName}.{method.Name}. " +
                        "Operation names must be unique across controllers on one endpoint; use [SoapOperation(\"...\")] to disambiguate.");
                owners[op] = $"{controllerName}.{method.Name}";
                map[op] = new MethodEntry(type, method);
            }
        }
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static bool IsSimpleType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(Guid) || t.IsEnum;
    }

    private static object? GetTaskResult(Task task)
    {
        var type = task.GetType();
        return type.IsGenericType ? type.GetProperty("Result")?.GetValue(task) : null;
    }

    internal sealed record MethodEntry(Type ControllerType, MethodInfo Method);
}
