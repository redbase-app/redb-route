using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Serialization;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that marshals (serializes) the exchange body using a registered <see cref="IMessageSerializer"/>.
/// </summary>
public sealed class MarshalDefinition : ProcessorDefinition
{
    private readonly Type _serializerType;

    /// <summary>Creates a marshal definition using the specified serializer type.</summary>
    /// <param name="serializerType">Serializer type that implements <see cref="IMessageSerializer"/>; must have a default constructor.</param>
    public MarshalDefinition(Type serializerType)
    {
        ArgumentNullException.ThrowIfNull(serializerType);
        _serializerType = serializerType;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var serializer = (IMessageSerializer)Activator.CreateInstance(_serializerType)!;
        return new MarshalProcessor(serializer);
    }
}

/// <summary>
/// Leaf definition that unmarshals (deserializes) the exchange body using a registered <see cref="IMessageSerializer"/>.
/// </summary>
public sealed class UnmarshalDefinition : ProcessorDefinition
{
    private readonly Type _serializerType;
    private readonly Type _targetType;

    /// <summary>Creates an unmarshal definition.</summary>
    /// <param name="serializerType">Serializer type; must have a default constructor.</param>
    /// <param name="targetType">Target deserialization type.</param>
    public UnmarshalDefinition(Type serializerType, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(serializerType);
        ArgumentNullException.ThrowIfNull(targetType);
        _serializerType = serializerType;
        _targetType = targetType;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var serializer = (IMessageSerializer)Activator.CreateInstance(_serializerType)!;
        return new UnmarshalProcessor(serializer, _targetType);
    }
}

/// <summary>
/// Leaf definition that converts the exchange body to a target type via <see cref="IDataFormatRegistry"/>.
/// </summary>
public sealed class ConvertBodyDefinition : ProcessorDefinition
{
    private readonly Type _targetType;

    /// <summary>Creates a convert-body definition.</summary>
    /// <param name="targetType">Type to convert the body to.</param>
    public ConvertBodyDefinition(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        _targetType = targetType;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new ConvertBodyProcessor(_targetType, context.GetService<IDataFormatRegistry>());
}
