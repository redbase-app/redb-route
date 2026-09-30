using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;

namespace redb.Route.Xml;

/// <summary>
/// A <c>&lt;list&gt;</c> on its way to a target: the items are raw bean values, the element type
/// is the one <c>of=</c> named (null when the target supplies it).
/// </summary>
internal sealed record BeanListValue(Type? ElementType, IReadOnlyList<object?> Items);

/// <summary>
/// Builds bean instances from raw values — the one place both the <c>&lt;bean&gt;</c> section
/// and generated code (<see cref="XmlBeans"/>) go through, so a list, a reference or a text
/// value binds the same way in either. A raw value is a <see cref="string"/> (converted to the
/// target type after placeholder resolution), a <see cref="BeanListValue"/> (built into the
/// target's collection shape), or an object (assigned as it is, when the target accepts it).
/// Every failure is an <see cref="InvalidOperationException"/> naming the slot.
/// </summary>
internal static class BeanFactory
{
    /// <summary>The collection shapes a list builds into, for the messages.</summary>
    internal const string ListShapes =
        "T[], List<T>, IList<T>, ICollection<T>, IReadOnlyList<T>, IReadOnlyCollection<T>, IEnumerable<T>";

    /// <summary>
    /// Creates the instance: through <paramref name="factoryMethod"/> (a public static creator whose
    /// parameters type the arguments), through the constructor the argument types name, or through a
    /// constructor chosen by <see cref="ActivatorUtilities"/>, which needs the arguments already
    /// built — a list there must name its element type with <c>of=</c>.
    /// </summary>
    internal static object Instantiate(
        Type type, IReadOnlyList<object?> args, IReadOnlyList<Type>? argumentTypes, string? factoryMethod,
        IServiceProvider provider, Func<string, string> resolve)
    {
        if (factoryMethod is not null)
        {
            var method = FactoryMethod(type, factoryMethod, args.Count, argumentTypes);
            return Invoke(method, args, $"{factoryMethod} argument", resolve)
                   ?? throw new InvalidOperationException($"'{type.FullName}.{factoryMethod}' returned null.");
        }
        if (argumentTypes is not null)
        {
            // The signature is named: exactly that public constructor, each value bound to its
            // parameter's type — no guessing among overloads, no DI-supplied parameters.
            return Invoke(Constructor(type, args.Count, argumentTypes), args, "constructorArg", resolve)!;
        }
        if (args.Count == 0)
            return ActivatorUtilities.CreateInstance(provider, type);
        var built = new object[args.Count];
        for (var i = 0; i < args.Count; i++)
            built[i] = Materialize(args[i], $"constructorArg {i + 1}", resolve);
        return ActivatorUtilities.CreateInstance(provider, type, built);
    }

    /// <summary>
    /// The static creator the arguments call, the way C# resolves a call: the parameters after the
    /// given arguments must all be optional (their defaults are used). With argument types the
    /// given parameters must be exactly those types; without them every overload a markup value can
    /// reach counts, and more than one is refused — picking one would be a guess.
    /// </summary>
    internal static MethodInfo FactoryMethod(Type type, string name, int argumentCount, IReadOnlyList<Type>? argumentTypes)
        => (MethodInfo)Choose(type, type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name),
            argumentCount, argumentTypes, $"public static method '{name}'", $"public static methods '{name}'");

    /// <summary>The public constructor the named argument types call — the same rule as <see cref="FactoryMethod"/>.</summary>
    internal static ConstructorInfo Constructor(Type type, int argumentCount, IReadOnlyList<Type> argumentTypes)
        => (ConstructorInfo)Choose(type, type.GetConstructors(), argumentCount, argumentTypes,
            "public constructor", "public constructors");

    private static MethodBase Choose(Type type, IEnumerable<MethodBase> members, int count, IReadOnlyList<Type>? types,
        string one, string many)
    {
        var candidates = members.Where(m => Accepts(m.GetParameters(), count, types)).ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(types is null
                ? $"type '{type.FullName}' has no {one} taking {count} argument(s)."
                : $"type '{type.FullName}' has no {one} ({Signature(types)})."),
            _ => throw new InvalidOperationException(
                $"type '{type.FullName}' has {candidates.Count} {many} taking {count} argument(s) " +
                $"({string.Join("; ", candidates.Select(m => Signature([.. m.GetParameters().Select(p => p.ParameterType)])))}) — " +
                (types is null
                    ? "name the parameter types with <constructorArg type=…>."
                    : "they differ only in optional parameters, so the call is ambiguous even with the types named.")),
        };
    }

    /// <summary>
    /// Whether a member takes <paramref name="count"/> arguments: that many leading parameters, the
    /// rest optional. Named types must match exactly; otherwise a parameter no markup value can
    /// ever be (a by-ref-like type such as <c>ReadOnlySpan&lt;char&gt;</c>, a <c>ref</c>) rules the
    /// overload out — reflection cannot pass one, so it is not a candidate to be ambiguous with.
    /// </summary>
    private static bool Accepts(ParameterInfo[] parameters, int count, IReadOnlyList<Type>? types)
    {
        if (parameters.Length < count || parameters.Skip(count).Any(p => !p.IsOptional))
            return false;
        var given = parameters.Take(count).Select(p => p.ParameterType).ToList();
        return types is null
            ? given.All(t => !t.IsByRefLike && !t.IsByRef)
            : given.SequenceEqual(types);
    }

    /// <summary>Binds the given arguments to their parameters, fills the optional rest with defaults, calls.</summary>
    private static object? Invoke(MethodBase member, IReadOnlyList<object?> args, string label, Func<string, string> resolve)
    {
        var parameters = member.GetParameters();
        var bound = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            bound[i] = i < args.Count
                ? Bind(args[i], parameters[i].ParameterType, $"{label} {i + 1}", resolve)
                : DefaultOf(parameters[i]);
        return member is ConstructorInfo constructor ? constructor.Invoke(bound) : member.Invoke(null, bound);
    }

    /// <summary>
    /// An optional parameter's default as the call needs it. Reflection reports an enum default as
    /// its underlying number (X509KeyStorageFlags.DefaultKeySet reads as 0), so it is converted back.
    /// </summary>
    private static object? DefaultOf(ParameterInfo parameter)
    {
        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        if (!parameter.HasDefaultValue)
            return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
        return parameter.DefaultValue is { } value && type.IsEnum && !type.IsInstanceOfType(value)
            ? Enum.ToObject(type, value)
            : parameter.DefaultValue;
    }

    internal static string Signature(IReadOnlyList<Type> types) => string.Join(", ", types.Select(t => t.FullName ?? t.Name));


    /// <summary>Binds a raw value to a writable public property of the instance.</summary>
    internal static void Assign(object instance, string key, object? raw, Func<string, string> resolve)
    {
        var type = instance.GetType();
        var property = type.GetProperty(key);
        if (property is not { CanWrite: true })
            throw new InvalidOperationException($"type '{type.FullName}' has no writable public property '{key}'.");
        property.SetValue(instance, Bind(raw, property.PropertyType, key, resolve));
    }

    /// <summary>The element type a list builds for <paramref name="target"/>, or null when it is not a list shape.</summary>
    internal static Type? ElementTypeOf(Type target)
    {
        if (target.IsArray)
            return target.GetArrayRank() == 1 ? target.GetElementType() : null;
        if (!target.IsGenericType)
            return null;
        var definition = target.GetGenericTypeDefinition();
        return definition == typeof(List<>)
               || definition == typeof(IList<>)
               || definition == typeof(ICollection<>)
               || definition == typeof(IReadOnlyList<>)
               || definition == typeof(IReadOnlyCollection<>)
               || definition == typeof(IEnumerable<>)
            ? target.GetGenericArguments()[0]
            : null;
    }

    /// <summary>
    /// Converts a raw value for a target of a known type: text through the shared option converter,
    /// a list into the target's collection shape, an object when the target accepts it.
    /// </summary>
    internal static object? Bind(object? raw, Type target, string slot, Func<string, string> resolve)
    {
        switch (raw)
        {
            case string text:
            {
                var resolved = resolve(text);
                return OptionValueConverter.Convert(resolved, target)
                       ?? throw new InvalidOperationException($"'{resolved}' is not convertible to {target.Name} for '{slot}'.");
            }
            case BeanListValue list:
            {
                var element = ElementTypeOf(target)
                              ?? throw new InvalidOperationException(NotAListShape(target, slot));
                if (list.ElementType is not null && list.ElementType != element)
                    throw new InvalidOperationException(ElementTypeDiffers(list.ElementType, element, slot));
                return Build(target, element, list, slot, resolve);
            }
            case null:
                throw new InvalidOperationException($"'{slot}' got no value.");
            default:
                if (!target.IsInstanceOfType(raw))
                    throw new InvalidOperationException(NotAssignable(raw.GetType(), target, slot));
                return raw;
        }
    }

    /// <summary>
    /// A constructor argument before the constructor is chosen: text stays text (after
    /// placeholders), an object stays itself, a list builds as <c>T[]</c> of its <c>of=</c> type
    /// — an array passes wherever a constructor takes T[], IEnumerable&lt;T&gt; or IReadOnlyList&lt;T&gt;.
    /// </summary>
    private static object Materialize(object? raw, string slot, Func<string, string> resolve) => raw switch
    {
        string text => resolve(text),
        BeanListValue { ElementType: { } element } list => Build(element.MakeArrayType(), element, list, slot, resolve),
        BeanListValue => throw new InvalidOperationException(ConstructorListNeedsOf(slot)),
        null => throw new InvalidOperationException($"'{slot}' got no value."),
        _ => raw,
    };

    // The same mistakes found before load: the package gate reports them in these words.

    internal static string NotAListShape(Type target, string slot)
        => $"'{slot}' is {target.Name}, which a <list> does not build into (valid: {ListShapes}).";

    internal static string ElementTypeDiffers(Type named, Type element, string slot)
        => $"<list of=\"{named.FullName}\"> differs from the element type {element.FullName} of '{slot}'.";

    internal static string NotAssignable(Type actual, Type target, string slot)
        => $"'{actual.FullName}' is not assignable to {target.Name} for '{slot}'.";

    internal static string ConstructorListNeedsOf(string slot)
        => $"'{slot}': a <list> passed to a constructor names its element type with of= — the constructor is not chosen yet, so nothing else types it.";

    private static object Build(Type target, Type element, BeanListValue list, string slot, Func<string, string> resolve)
    {
        var items = Array.CreateInstance(element, list.Items.Count);
        for (var i = 0; i < list.Items.Count; i++)
            items.SetValue(Bind(list.Items[i], element, $"{slot}[{i}]", resolve), i);
        if (target.IsArray)
            return items;
        // Every interface shape and List<T> itself take a List<T>: it is the one that also
        // honours IList<T>/ICollection<T> writes, which an array refuses.
        return Activator.CreateInstance(typeof(List<>).MakeGenericType(element), items)!;
    }
}
