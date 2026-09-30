using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Xml;

/// <summary>One endpoint option in the catalog: name, kind, default, secrecy, enum hints, the side that reads it.</summary>
/// <param name="Name">The option's property name (PascalCase; the URI and the markup take it case-insensitively).</param>
/// <param name="Type">The value kind: bool, int, long, double, duration, enum (one member), flags (members joined by commas) or string.</param>
/// <param name="Default">The default rendered invariantly, or null when unknown.</param>
/// <param name="Sensitive">A secret (<c>[Sensitive]</c>): redacted wherever the URI is shown.</param>
/// <param name="EnumValues">The member names of an enum option, else null.</param>
/// <param name="Role">The only side of the endpoint that reads the option (<see cref="EndpointOptions.RoleOf"/>):
/// a producer-only option on a <c>&lt;from&gt;</c> is refused, and the other way round. Null — both sides read it.</param>
/// <param name="ConnectionParameter">The connection factory sets it (<c>[ConnectionParameter]</c>): beside the option that names
/// the factory it is refused, the factory being the whole connection.</param>
/// <param name="ConnectionFactoryReference">This option names the connection factory (<c>[ConnectionFactoryReference]</c>).</param>
public sealed record CatalogOption(
    string Name,
    string Type,
    string? Default,
    bool Sensitive,
    IReadOnlyList<string>? EnumValues,
    EndpointRole? Role,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ConnectionParameter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ConnectionFactoryReference);

/// <summary>One component in the catalog — everything the editor's property panel shows (Ф4 §4).</summary>
/// <param name="Scheme">The URI scheme and the structured-form element name.</param>
/// <param name="AlternateSchemes">Other schemes the component answers to.</param>
/// <param name="Package">The assembly the component lives in.</param>
/// <param name="OptionsType">The full name of its options class, or null when it has none.</param>
/// <param name="PathSynonym">The attribute the structured form accepts for the path (kafka: topic), or null.</param>
/// <param name="PathIsText">The path is the element's text content (the SQL-style connectors).</param>
/// <param name="Options">Its options, by name.</param>
/// <param name="Lenient">The connector takes parameters it has no option for (<see cref="EndpointOptions.IsLenient"/>):
/// false = an unknown name is refused when the endpoint is created, so the schema and the package gate refuse it too.</param>
public sealed record CatalogComponent(
    string Scheme,
    IReadOnlyList<string> AlternateSchemes,
    string? Package,
    string? OptionsType,
    string? PathSynonym,
    bool PathIsText,
    IReadOnlyList<CatalogOption> Options,
    bool Lenient);

/// <summary>
/// The component catalog (Ф4 §4): built by REFLECTION from what already exists — the
/// component's <see cref="IComponent.Scheme"/>, its one-line §7.2 metadata, and its
/// <see cref="EndpointOptions"/> subclass (found in the component's assembly by naming, else
/// as the single candidate). Nothing here is hand-kept per connector: a new component with
/// Options and Scheme appears in the catalog, the structured form and the generated XSD with
/// zero XML-specific code — the anti-copy-paste rule by construction.
/// </summary>
public static class ComponentCatalog
{
    /// <summary>Builds catalog records for the given components, ordered by scheme.</summary>
    public static IReadOnlyList<CatalogComponent> Build(IEnumerable<IComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var records = new List<CatalogComponent>();
        foreach (var component in components.GroupBy(c => c.Scheme).Select(g => g.First()))
        {
            var type = component.GetType();
            var optionsType = FindOptionsType(type);
            records.Add(new CatalogComponent(
                component.Scheme,
                [.. component.AlternateSchemes],
                type.Assembly.GetName().Name,
                optionsType?.FullName,
                (component as ComponentBase)?.StructuredPathSynonym,
                (component as ComponentBase)?.PathIsText ?? false,
                optionsType is null ? [] : HarvestOptions(optionsType),
                // No options class: nothing binds the parameters, so nothing refuses them either.
                optionsType is null || EndpointOptions.IsLenient(optionsType)));
        }
        return [.. records.OrderBy(r => r.Scheme, StringComparer.Ordinal)];
    }

    /// <summary>The catalog as pretty JSON — the file the editor tooling reads.</summary>
    public static string ToJson(IReadOnlyList<CatalogComponent> catalog)
        => JsonSerializer.Serialize(catalog, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// The component's options class: by naming (<c>XComponent</c> → <c>XEndpointOptions</c> /
    /// <c>XOptions</c>) in the component's assembly, else the assembly's single
    /// <see cref="EndpointOptions"/> subclass, else none (an options-less component is honest).
    /// </summary>
    /// <summary>The TOptions of an endpoint type deriving from <c>EndpointBase&lt;TOptions&gt;</c>, or null.</summary>
    private static Type? DeclaredOptions(Type endpointType)
    {
        for (var type = endpointType; type is not null && type != typeof(object); type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EndpointBase<>))
                return type.GetGenericArguments()[0];
        }
        return null;
    }

    private static Type? FindOptionsType(Type componentType)
    {
        var candidates = componentType.Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(EndpointOptions).IsAssignableFrom(t))
            .ToList();
        if (candidates.Count == 0)
            return null;
        // The naming walk climbs the BASE chain too: HttpsComponent : HttpComponent finds
        // HttpEndpointOptions — a derived component inherits its base's options.
        for (var type = componentType; type is not null && type != typeof(object); type = type.BaseType)
        {
            var stem = type.Name.EndsWith("Component", StringComparison.Ordinal)
                ? type.Name[..^"Component".Length]
                : type.Name;
            // The endpoint the component creates declares its options as a type argument
            // (ImapEndpoint : EndpointBase<MailEndpointOptions>) — the code says it, no name to
            // match. Three mail components share one options class, which naming never found.
            if (componentType.Assembly.GetType($"{type.Namespace}.{stem}Endpoint") is { } endpoint
                && DeclaredOptions(endpoint) is { } declared)
                return declared;
            var byName = candidates.FirstOrDefault(t => t.Name == stem + "EndpointOptions")
                ?? candidates.FirstOrDefault(t => t.Name == stem + "Options");
            if (byName is not null)
                return byName;
        }
        // The single-candidate fallback is safe only in a single-component assembly —
        // otherwise a neighbour's options would silently attach to the wrong scheme.
        var componentCount = componentType.Assembly.GetTypes()
            .Count(t => !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t));
        return candidates.Count == 1 && componentCount == 1 ? candidates[0] : null;
    }

    private static IReadOnlyList<CatalogOption> HarvestOptions(Type optionsType)
    {
        object? defaults;
        try
        {
            defaults = Activator.CreateInstance(optionsType);
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or TargetInvocationException)
        {
            defaults = null; // no parameterless constructor — defaults stay unknown
        }

        var options = new List<CatalogOption>();
        foreach (var property in optionsType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            // Only what the connector declares: the EndpointOptions base carries machinery
            // (UnmappedParameters, …), not options.
            if (property.DeclaringType == typeof(EndpointOptions) || !property.CanWrite)
                continue;
            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            options.Add(new CatalogOption(
                property.Name,
                TypeLabel(underlying),
                Render(defaults is null ? null : property.GetValue(defaults)),
                property.GetCustomAttribute<SensitiveAttribute>() is not null,
                underlying.IsEnum ? Enum.GetNames(underlying) : null,
                EndpointOptions.RoleOf(property),
                property.IsDefined(typeof(ConnectionParameterAttribute), inherit: true),
                property.IsDefined(typeof(ConnectionFactoryReferenceAttribute), inherit: true)));
        }
        return [.. options.OrderBy(o => o.Name, StringComparer.Ordinal)];
    }

    private static string TypeLabel(Type type)
    {
        // A [Flags] enum takes several members at once ("Tls12,Tls13"), so it is not a pick-one list.
        if (type.IsEnum) return type.IsDefined(typeof(FlagsAttribute), inherit: false) ? "flags" : "enum";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "double";
        if (type == typeof(TimeSpan)) return "duration";
        return "string";
    }

    private static string? Render(object? value) => value switch
    {
        null => null,
        bool flag => flag ? "true" : "false",
        Enum member => member.ToString(),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        string text => text,
        _ => null,
    };
}
