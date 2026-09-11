using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Serialization;

namespace redb.Route.Definitions;

/// <summary>
/// Resolves the serializer of a <c>Marshal</c> / <c>Unmarshal</c> node from whichever form the DSL
/// used: an instance (options per node), a content type (looked up in <see cref="IDataFormatRegistry"/>
/// at route build — an unregistered format fails <c>Start()</c>), or a serializer type (instantiated
/// with its parameterless constructor).
/// </summary>
internal static class DataFormatResolution
{
    public static IMessageSerializer Resolve(IRouteContext context, Type? serializerType, IMessageSerializer? serializer, string? contentType)
    {
        if (serializer is not null)
            return serializer;

        if (contentType is not null)
        {
            var registry = context.GetService<IDataFormatRegistry>()
                ?? throw new InvalidOperationException("IDataFormatRegistry is not available on the route context.");
            return registry.GetSerializer(contentType)
                ?? throw new InvalidOperationException(
                    $"Data format '{contentType}' is not registered. Register it with context.AddDataFormat(...) " +
                    "(a format package offers e.g. AddCsvDataFormat()) or pass the serializer instance: Marshal(new CsvDataFormat(...)).");
        }

        return (IMessageSerializer)Activator.CreateInstance(serializerType!)!;
    }
}

/// <summary>
/// Leaf definition that serializes the exchange body with an <see cref="IMessageSerializer"/>:
/// by type (<c>Marshal&lt;JsonMessageSerializer&gt;()</c>), by instance (<c>Marshal(new CsvDataFormat(o))</c>)
/// or by registered content type (<c>Marshal("text/csv")</c>).
/// </summary>
public sealed class MarshalDefinition : ProcessorDefinition
{
    private readonly Type? _serializerType;
    private readonly IMessageSerializer? _serializer;

    /// <summary>Serializer created from its parameterless constructor at route build.</summary>
    public MarshalDefinition(Type serializerType)
    {
        ArgumentNullException.ThrowIfNull(serializerType);
        _serializerType = serializerType;
    }

    /// <summary>A configured serializer instance (per-node options).</summary>
    public MarshalDefinition(IMessageSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _serializer = serializer;
    }

    /// <summary>A content type resolved in the context's <see cref="IDataFormatRegistry"/> at route build.</summary>
    public MarshalDefinition(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ContentType = contentType;
    }

    /// <summary>Content type used for registry resolution, when the node was declared that way (XML form <c>format=</c>).</summary>
    public string? ContentType { get; }

    /// <inheritdoc/>
    public override IProcessor CreateProcessor(IRouteContext context)
        => new MarshalProcessor(DataFormatResolution.Resolve(context, _serializerType, _serializer, ContentType));
}

/// <summary>
/// Leaf definition that deserializes the exchange body to <see cref="TargetType"/> with an
/// <see cref="IMessageSerializer"/> chosen by type, by instance or by registered content type.
/// </summary>
public sealed class UnmarshalDefinition : ProcessorDefinition
{
    private readonly Type? _serializerType;
    private readonly IMessageSerializer? _serializer;

    /// <summary>Serializer created from its parameterless constructor at route build.</summary>
    public UnmarshalDefinition(Type serializerType, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(serializerType);
        ArgumentNullException.ThrowIfNull(targetType);
        _serializerType = serializerType;
        TargetType = targetType;
    }

    /// <summary>A configured serializer instance (per-node options).</summary>
    public UnmarshalDefinition(IMessageSerializer serializer, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(targetType);
        _serializer = serializer;
        TargetType = targetType;
    }

    /// <summary>A content type resolved in the context's <see cref="IDataFormatRegistry"/> at route build.</summary>
    public UnmarshalDefinition(string contentType, Type targetType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(targetType);
        ContentType = contentType;
        TargetType = targetType;
    }

    /// <summary>Content type used for registry resolution, when the node was declared that way (XML form <c>format=</c>).</summary>
    public string? ContentType { get; }

    /// <summary>CLR type the body is deserialized to.</summary>
    public Type TargetType { get; }

    /// <inheritdoc/>
    public override IProcessor CreateProcessor(IRouteContext context)
        => new UnmarshalProcessor(DataFormatResolution.Resolve(context, _serializerType, _serializer, ContentType), TargetType);
}

/// <summary>
/// Leaf definition that converts the exchange body to a target type via <see cref="IDataFormatRegistry"/>
/// (resolved from the message content type) or the built-in conversions.
/// </summary>
public sealed class ConvertBodyDefinition : ProcessorDefinition
{
    private readonly Type _targetType;

    /// <summary>Creates the node.</summary>
    public ConvertBodyDefinition(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        _targetType = targetType;
    }

    /// <inheritdoc/>
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ConvertBodyProcessor(_targetType, context.GetService<IDataFormatRegistry>());
}
