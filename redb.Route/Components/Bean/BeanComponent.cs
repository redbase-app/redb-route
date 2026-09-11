using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Components.Bean;

/// <summary>
/// The <c>bean:</c> component (Route-XML Ф1.2, decision Р8): a reference to user code as an
/// ordinary endpoint, so declarative routes never carry <c>class=</c>/<c>ref=</c> attributes.
/// Two URI forms:
/// <code>
/// bean:#orderEnricher?method=Enrich                          — object from the context registry
/// bean:Acme.Orders.EnrichProcessor, Acme.Orders?timeoutMs=5  — instance created per endpoint
/// </code>
/// The instance lives as long as the endpoint (endpoints are cached by normalized URI, so equal
/// URIs share one instance and different parameter sets get their own). Unrecognised query
/// parameters bind to the instance's public settable properties through the same converter the
/// endpoint options use; <c>[Sensitive]</c>-marked properties join the URI redaction set. The
/// target method is picked once and compiled into a delegate — reflection never runs per message
/// (Route-XML Ф2 §3.5).
/// </summary>
public sealed class BeanComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, BeanEndpoint> _endpoints = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override string Scheme => "bean";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return _endpoints.GetOrAdd(uri.NormalizedKey, _ => BuildEndpoint(uri));
    }

    private BeanEndpoint BuildEndpoint(EndpointUri uri)
    {
        var reference = uri.Path?.Trim();
        if (string.IsNullOrEmpty(reference))
            throw new ArgumentException(
                "bean: needs a target — 'bean:#registryName' or 'bean:Namespace.Type, AssemblyName'.");

        var options = new BeanEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        var instance = reference.StartsWith('#')
            ? ResolveFromRegistry(reference)
            : CreateFromTypeName(reference, options.UnmappedParameters);

        var invoker = BeanInvoker.For(instance.GetType(), options.Method, reference);
        return new BeanEndpoint(uri, this, options, instance, invoker);
    }

    private object ResolveFromRegistry(string reference)
    {
        var context = Context ?? throw new InvalidOperationException(
            "bean: component has no context; register it via AddComponent.");
        return context.GetFromRegistry<object>(reference)
            ?? throw new InvalidOperationException(
                $"bean: object '{reference}' is not in the context registry. " +
                "Register it with AddToRegistry (or a <bean> declaration) before Start().");
    }

    private object CreateFromTypeName(string typeName, IReadOnlyDictionary<string, string> properties)
    {
        if (typeName.Contains("Version=", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("PublicKeyToken=", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Culture=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"bean: type name '{typeName}' pins an assembly version/key. Use 'Namespace.Type, AssemblyName' " +
                "only — a pinned version silently breaks the route on every assembly bump.");

        var resolver = Context?.GetService<IBeanTypeResolver>() ?? DefaultBeanTypeResolver.Instance;
        var type = resolver.Resolve(typeName)
            ?? throw new InvalidOperationException(
                $"bean: type '{typeName}' was not found in the loaded assemblies. " +
                "Check the 'Namespace.Type, AssemblyName' spelling and that the assembly is loaded.");

        if (!(type.IsPublic || type.IsNestedPublic))
            throw new InvalidOperationException(
                $"bean: type '{type.FullName}' was found but is not public. Module discovery and bean: " +
                "work through exported types only — make the class public.");

        var provider = Context?.GetServiceProvider() ?? EmptyServiceProvider.Instance;
        var instance = ActivatorUtilities.CreateInstance(provider, type);

        BindProperties(instance, type, properties);
        return instance;
    }

    private static void BindProperties(object instance, Type type, IReadOnlyDictionary<string, string> properties)
    {
        if (properties.Count == 0)
            return;

        EndpointOptions.RegisterSensitiveKeysFor(type);
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var (key, rawValue) in properties)
        {
            var prop = Array.Find(props, p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (prop is null || !prop.CanWrite)
                throw new ArgumentException(
                    $"bean: type '{type.FullName}' has no writable public property '{key}'. " +
                    $"Writable properties: {string.Join(", ", props.Where(p => p.CanWrite).Select(p => p.Name))}.");

            var converted = OptionValueConverter.Convert(rawValue, prop.PropertyType)
                ?? throw new ArgumentException(
                    $"bean: value '{rawValue}' is not convertible to {prop.PropertyType.Name} " +
                    $"for property '{type.FullName}.{prop.Name}'.");
            prop.SetValue(instance, converted);
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>Options for <c>bean:</c> endpoints. Everything unmapped binds to the bean instance.</summary>
public sealed class BeanEndpointOptions : EndpointOptions
{
    /// <summary>
    /// Target method name. Optional when the type has exactly one suitable public method; with
    /// several, the error lists the candidates.
    /// </summary>
    public string? Method { get; set; }

    /// <inheritdoc />
    public override void Validate() { }
}

/// <summary>Endpoint holding the bean instance and its compiled invoker.</summary>
public sealed class BeanEndpoint : EndpointBase<BeanEndpointOptions>
{
    internal object Instance { get; }
    internal BeanInvoker Invoker { get; }

    internal BeanEndpoint(EndpointUri uri, BeanComponent component, BeanEndpointOptions options,
        object instance, BeanInvoker invoker)
        : base(uri, component, options)
    {
        Instance = instance;
        Invoker = invoker;
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new BeanProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => throw new NotSupportedException("bean: endpoints are producers only — use them as To() destinations.");
}

/// <summary>Producer invoking the bean's compiled delegate per exchange.</summary>
public sealed class BeanProducer : IProducer
{
    private readonly BeanEndpoint _endpoint;

    /// <summary>Creates the producer for a bean endpoint.</summary>
    public BeanProducer(BeanEndpoint endpoint)
        => _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var invoker = _endpoint.Invoker;
        var raw = invoker.Invoke(_endpoint.Instance, exchange, ct);

        if (raw is Task task)
        {
            await task.ConfigureAwait(false);
            if (invoker.TaskResult is { } getResult && getResult(task) is { } asyncResult)
                exchange.In.Body = asyncResult;
            return;
        }

        // Camel bean: semantics — a non-null return value (other than Task) becomes the body.
        if (invoker.ReturnsValue && raw is not null)
            exchange.In.Body = raw;
    }
}

/// <summary>
/// The bean's target method, selected once and compiled into a delegate at endpoint creation:
/// reflection never runs on the message path (Route-XML Ф2 §3.5).
/// </summary>
public sealed partial class BeanInvoker
{
    /// <summary>Raw invocation: returns the method's result (a Task for async methods, null for void).</summary>
    internal Func<object, IExchange, CancellationToken, object?> Invoke { get; }

    /// <summary>True when the method returns a plain (non-Task) value that should become the body.</summary>
    internal bool ReturnsValue { get; }

    /// <summary>For <c>Task&lt;T&gt;</c> methods: reads the completed task's result. Null otherwise.</summary>
    internal Func<Task, object?>? TaskResult { get; }

    private BeanInvoker(Func<object, IExchange, CancellationToken, object?> invoke, bool returnsValue,
        Func<Task, object?>? taskResult)
    {
        Invoke = invoke;
        ReturnsValue = returnsValue;
        TaskResult = taskResult;
    }

    /// <summary>
    /// Selects the target method — by <paramref name="methodName"/> when given, otherwise the
    /// single suitable public method — and compiles the invoker. A suitable method takes
    /// <c>(IExchange)</c> or <c>(IExchange, CancellationToken)</c>; the two-parameter form wins
    /// when both names match. A <c>method=name(arg, …)</c> specification switches to Camel-style
    /// parameter binding (Route-XML Ф1.6): each argument is a route-language expression and the
    /// method's parameters carry ordinary types.
    /// </summary>
    internal static BeanInvoker For(Type type, string? methodName, string reference)
    {
        if (methodName is not null && methodName.Contains('('))
            return ForBound(type, methodName, reference);

        var suitable = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(IsSuitable)
            .OrderByDescending(m => m.GetParameters().Length) // (IExchange, ct) preferred
            .ToList();

        if (suitable.Count == 0)
            throw new InvalidOperationException(
                $"bean: '{reference}' ({type.FullName}) has no suitable public method. Expected a public " +
                "instance method taking (IExchange) or (IExchange, CancellationToken).");

        MethodInfo method;
        if (methodName is not null)
        {
            method = suitable.FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"bean: '{reference}' has no suitable method '{methodName}'. " +
                    $"Suitable methods: {Describe(suitable)}.");
        }
        else
        {
            var distinctNames = suitable.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctNames.Count > 1)
                throw new InvalidOperationException(
                    $"bean: '{reference}' has several suitable methods — name one with ?method=. " +
                    $"Candidates: {Describe(suitable)}.");
            method = suitable[0];
        }

        return Compile(type, method);
    }

    private static bool IsSuitable(MethodInfo m)
    {
        if (m.IsSpecialName || m.DeclaringType == typeof(object))
            return false;
        var p = m.GetParameters();
        return p.Length switch
        {
            1 => p[0].ParameterType == typeof(IExchange),
            2 => p[0].ParameterType == typeof(IExchange) && p[1].ParameterType == typeof(CancellationToken),
            _ => false,
        };
    }

    private static string Describe(IEnumerable<MethodInfo> methods)
        => string.Join(", ", methods.Select(m =>
            $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})"));

    private static BeanInvoker Compile(Type type, MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var exchange = Expression.Parameter(typeof(IExchange), "exchange");
        var ct = Expression.Parameter(typeof(CancellationToken), "ct");

        var args = method.GetParameters().Length == 2
            ? new Expression[] { exchange, ct }
            : [exchange];
        Expression call = Expression.Call(Expression.Convert(instance, type), method, args);

        Expression body = method.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));

        var invoke = Expression
            .Lambda<Func<object, IExchange, CancellationToken, object?>>(body, instance, exchange, ct)
            .Compile();

        var (returnsValue, taskResult) = ClassifyReturn(method.ReturnType);
        return new BeanInvoker(invoke, returnsValue, taskResult);
    }
}

// ── Camel-style parameter binding (Route-XML Ф1.6) ───────────────────────────

public sealed partial class BeanInvoker
{
    // (partial: the base selection/compilation half lives above in this file)

    /// <summary>
    /// Builds the invoker for a <c>method=name(arg, …)</c> specification. Each argument is a
    /// route-language expression compiled at endpoint creation (a malformed one fails the build);
    /// the overload is picked by argument count, with an optional trailing
    /// <see cref="CancellationToken"/> parameter filled automatically. Argument values convert to
    /// the parameter types through the shared option converter; a value already of the parameter
    /// type passes as-is. Boundary: an argument must not contain a bare <c>&amp;</c> — the URI
    /// query splits on it; write the word form <c>AND</c>.
    /// </summary>
    private static BeanInvoker ForBound(Type type, string methodSpec, string reference)
    {
        var open = methodSpec.IndexOf('(');
        var name = methodSpec[..open].Trim();
        var closing = methodSpec.LastIndexOf(')');
        if (name.Length == 0 || closing < open)
            throw new ArgumentException(
                $"bean: malformed method specification '{methodSpec}' for '{reference}'. Expected name(arg, ...).");

        var argTexts = SplitArguments(methodSpec[(open + 1)..closing]);
        var expressions = argTexts.Select(t => new redb.Route.Expressions.StringExpression(t)).ToArray();

        var candidates = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)
                        && m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Where(m =>
            {
                var p = m.GetParameters();
                return p.Length == argTexts.Count
                    || (p.Length == argTexts.Count + 1 && p[^1].ParameterType == typeof(CancellationToken));
            })
            .OrderByDescending(m => m.GetParameters().Length) // the ct-taking overload wins
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"bean: '{reference}' has no public method '{name}' taking {argTexts.Count} argument(s) " +
                "(an optional trailing CancellationToken does not count).");
        if (candidates.Count > 1 && candidates[0].GetParameters().Length == candidates[1].GetParameters().Length)
            throw new InvalidOperationException(
                $"bean: '{reference}' has several '{name}' overloads with {argTexts.Count} argument(s) — " +
                $"parameter binding picks by count only. Candidates: {Describe(candidates)}.");

        var method = candidates[0];
        var parameters = method.GetParameters();
        var takesCt = parameters.Length == argTexts.Count + 1;

        var converters = new Func<object?, object?>[argTexts.Count];
        for (var i = 0; i < argTexts.Count; i++)
        {
            var parameter = parameters[i];
            var target = parameter.ParameterType;
            var where = $"parameter '{parameter.Name}' of {type.Name}.{method.Name}";
            converters[i] = value => ConvertArgument(value, target, where);
        }

        var call = CompileBoundCall(type, method, takesCt);
        object? Invoke(object instance, IExchange exchange, CancellationToken ct)
        {
            var args = new object?[expressions.Length];
            for (var i = 0; i < expressions.Length; i++)
                args[i] = converters[i](expressions[i].Evaluate<object>(exchange));
            return call(instance, args, ct);
        }

        var (returnsValue, taskResult) = ClassifyReturn(method.ReturnType);
        return new BeanInvoker(Invoke, returnsValue, taskResult);
    }

    /// <summary>Splits top-level commas, respecting quotes (with backslash escapes) and nesting.</summary>
    private static List<string> SplitArguments(string text)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return args;

        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == '\\') i++;
                else if (c == q) quote = null;
                continue;
            }
            switch (c)
            {
                case '\'' or '"': quote = c; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0:
                    args.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        args.Add(text[start..].Trim());
        return args;
    }

    private static object? ConvertArgument(object? value, Type target, string where)
    {
        if (value is null)
        {
            if (!target.IsValueType || Nullable.GetUnderlyingType(target) is not null)
                return null;
            throw new InvalidOperationException(
                $"bean: the argument for {where} evaluated to null, but {target.Name} is a value type.");
        }
        if (target.IsInstanceOfType(value))
            return value;
        if (value is string text)
            return OptionValueConverter.Convert(text, target)
                ?? throw new InvalidOperationException(
                    $"bean: the argument value '{text}' is not convertible to {target.Name} for {where}.");
        try
        {
            return System.Convert.ChangeType(value, Nullable.GetUnderlyingType(target) ?? target,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                $"bean: the argument value '{value}' ({value.GetType().Name}) is not convertible to " +
                $"{target.Name} for {where}.", ex);
        }
    }

    private static Func<object, object?[], CancellationToken, object?> CompileBoundCall(
        Type type, MethodInfo method, bool takesCt)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var args = Expression.Parameter(typeof(object?[]), "args");
        var ct = Expression.Parameter(typeof(CancellationToken), "ct");

        var parameters = method.GetParameters();
        var callArgs = new List<Expression>(parameters.Length);
        var bindable = takesCt ? parameters.Length - 1 : parameters.Length;
        for (var i = 0; i < bindable; i++)
            callArgs.Add(Expression.Convert(
                Expression.ArrayIndex(args, Expression.Constant(i)), parameters[i].ParameterType));
        if (takesCt)
            callArgs.Add(ct);

        Expression call = Expression.Call(Expression.Convert(instance, type), method, callArgs);
        Expression body = method.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));

        return Expression
            .Lambda<Func<object, object?[], CancellationToken, object?>>(body, instance, args, ct)
            .Compile();
    }

    /// <summary>Shared return-shape classification for both invoker forms.</summary>
    private static (bool ReturnsValue, Func<Task, object?>? TaskResult) ClassifyReturn(Type returnType)
    {
        if (returnType == typeof(void) || returnType == typeof(Task))
            return (false, null);
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var task = Expression.Parameter(typeof(Task), "task");
            var result = Expression.Property(Expression.Convert(task, returnType), "Result");
            var getter = Expression
                .Lambda<Func<Task, object?>>(Expression.Convert(result, typeof(object)), task)
                .Compile();
            return (false, getter);
        }
        return (true, null);
    }
}

/// <summary>
/// Default <see cref="IBeanTypeResolver"/>: <c>Type.GetType</c> first (the default load context),
/// then every loaded assembly — which includes collectible load contexts, so a type inside an
/// already-loaded plugin is found even without a host-specific resolver.
/// </summary>
public sealed class DefaultBeanTypeResolver : IBeanTypeResolver
{
    /// <summary>The shared instance used when no resolver is registered on the context.</summary>
    public static DefaultBeanTypeResolver Instance { get; } = new();

    /// <inheritdoc />
    public Type? Resolve(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        var direct = Type.GetType(typeName, throwOnError: false);
        if (direct is not null)
            return direct;

        var comma = typeName.IndexOf(',');
        var fullName = (comma >= 0 ? typeName[..comma] : typeName).Trim();
        var assemblyName = comma >= 0 ? typeName[(comma + 1)..].Trim() : null;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assemblyName is not null &&
                !string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                continue;
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null)
                return type;
        }

        return null;
    }
}
