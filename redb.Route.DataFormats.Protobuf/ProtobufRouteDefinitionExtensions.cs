using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.DataFormats.Protobuf;

/// <summary>DSL and registration sugar for <see cref="ProtobufDataFormat"/>.</summary>
public static class ProtobufRouteDefinitionExtensions
{
    /// <summary>Marshals the <c>IMessage</c> body to Protobuf bytes.</summary>
    public static IRouteDefinition MarshalProtobuf(this IRouteDefinition route, Action<ProtobufDataFormatOptions>? configure = null)
        => route.Marshal(new ProtobufDataFormat(Build(configure)));

    /// <summary>Unmarshals Protobuf bytes to the generated message type <typeparamref name="T"/>.</summary>
    public static IRouteDefinition UnmarshalProtobuf<T>(this IRouteDefinition route, Action<ProtobufDataFormatOptions>? configure = null)
        => route.Unmarshal<T>(new ProtobufDataFormat(Build(configure)));

    /// <summary>Registers Protobuf as <c>application/x-protobuf</c> on the context.</summary>
    public static IRouteContext AddProtobufDataFormat(this IRouteContext context, Action<ProtobufDataFormatOptions>? configure = null)
        => context.AddDataFormat(new ProtobufDataFormat(Build(configure)));

    /// <summary>DI form of <see cref="AddProtobufDataFormat(IRouteContext, Action{ProtobufDataFormatOptions})"/>.</summary>
    public static RedbRouteBuilder AddProtobufDataFormat(this RedbRouteBuilder builder, Action<ProtobufDataFormatOptions>? configure = null)
        => builder.AddDataFormat(new ProtobufDataFormat(Build(configure)));

    private static ProtobufDataFormatOptions Build(Action<ProtobufDataFormatOptions>? configure)
    {
        var options = new ProtobufDataFormatOptions();
        configure?.Invoke(options);
        return options;
    }
}
