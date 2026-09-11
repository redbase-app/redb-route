using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.DataFormats.Avro;

/// <summary>DSL and registration sugar for <see cref="AvroDataFormat"/>.</summary>
public static class AvroRouteDefinitionExtensions
{
    /// <summary>Marshals the body to Avro binary.</summary>
    public static IRouteDefinition MarshalAvro(this IRouteDefinition route, Action<AvroDataFormatOptions>? configure = null)
        => route.Marshal(new AvroDataFormat(Build(configure)));

    /// <summary>Unmarshals Avro binary to <typeparamref name="T"/>.</summary>
    public static IRouteDefinition UnmarshalAvro<T>(this IRouteDefinition route, Action<AvroDataFormatOptions>? configure = null)
        => route.Unmarshal<T>(new AvroDataFormat(Build(configure)));

    /// <summary>Registers Avro as <c>application/avro</c> on the context.</summary>
    public static IRouteContext AddAvroDataFormat(this IRouteContext context, Action<AvroDataFormatOptions>? configure = null)
        => context.AddDataFormat(new AvroDataFormat(Build(configure)));

    /// <summary>DI form of <see cref="AddAvroDataFormat(IRouteContext, Action{AvroDataFormatOptions})"/>.</summary>
    public static RedbRouteBuilder AddAvroDataFormat(this RedbRouteBuilder builder, Action<AvroDataFormatOptions>? configure = null)
        => builder.AddDataFormat(new AvroDataFormat(Build(configure)));

    private static AvroDataFormatOptions Build(Action<AvroDataFormatOptions>? configure)
    {
        var options = new AvroDataFormatOptions();
        configure?.Invoke(options);
        return options;
    }
}
