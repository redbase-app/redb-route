using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.DataFormats.Yaml;

/// <summary>DSL and registration sugar for <see cref="YamlDataFormat"/>.</summary>
public static class YamlRouteDefinitionExtensions
{
    /// <summary>Marshals the body to YAML.</summary>
    public static IRouteDefinition MarshalYaml(this IRouteDefinition route, Action<YamlDataFormatOptions>? configure = null)
        => route.Marshal(new YamlDataFormat(Build(configure)));

    /// <summary>Unmarshals YAML to <typeparamref name="T"/> (<c>object</c> gives a string-keyed dictionary / list tree).</summary>
    public static IRouteDefinition UnmarshalYaml<T>(this IRouteDefinition route, Action<YamlDataFormatOptions>? configure = null)
        => route.Unmarshal<T>(new YamlDataFormat(Build(configure)));

    /// <summary>Registers YAML as <c>application/yaml</c> (and its aliases) on the context.</summary>
    public static IRouteContext AddYamlDataFormat(this IRouteContext context, Action<YamlDataFormatOptions>? configure = null)
        => context.AddDataFormat(new YamlDataFormat(Build(configure)));

    /// <summary>DI form of <see cref="AddYamlDataFormat(IRouteContext, Action{YamlDataFormatOptions})"/>.</summary>
    public static RedbRouteBuilder AddYamlDataFormat(this RedbRouteBuilder builder, Action<YamlDataFormatOptions>? configure = null)
        => builder.AddDataFormat(new YamlDataFormat(Build(configure)));

    private static YamlDataFormatOptions Build(Action<YamlDataFormatOptions>? configure)
    {
        var options = new YamlDataFormatOptions();
        configure?.Invoke(options);
        return options;
    }
}
