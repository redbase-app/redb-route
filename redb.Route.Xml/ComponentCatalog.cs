using System.Globalization;
using System.Reflection;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Xml;

/// <summary>One endpoint option in the catalog: name, kind, default, secrecy, enum hints.</summary>
public sealed record CatalogOption(
    string Name,
    string Type,
    string? Default,
    bool Sensitive,
    IReadOnlyList<string>? EnumValues);

/// <summary>One component in the catalog — everything the editor's property panel shows (Ф4 §4).</summary>
public sealed record CatalogComponent(
    string Scheme,
    IReadOnlyList<string> AlternateSchemes,
    string? Package,
    string? OptionsType,
    string? PathSynonym,
    bool PathIsText,
    IReadOnlyList<CatalogOption> Options);

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
                optionsType is null ? [] : HarvestOptions(optionsType)));
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
    };

    /// <summary>
    /// The component's options class: by naming (<c>XComponent</c> → <c>XEndpointOptions</c> /
    /// <c>XOptions</c>) in the component's assembly, else the assembly's single
    /// <see cref="EndpointOptions"/> subclass, else none (an options-less component is honest).
    /// </summary>
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
                underlying.IsEnum ? Enum.GetNames(underlying) : null));
        }
        return [.. options.OrderBy(o => o.Name, StringComparer.Ordinal)];
    }

    private static string TypeLabel(Type type)
    {
        if (type.IsEnum) return "enum";
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
