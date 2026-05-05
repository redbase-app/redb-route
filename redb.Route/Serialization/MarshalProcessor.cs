using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Serialization;

/// <summary>
/// Processor that marshals the exchange body to/from bytes using an <see cref="IMessageSerializer"/>.
/// Use in a pipeline to serialize before sending or deserialize after receiving.
/// </summary>
public sealed class MarshalProcessor : IProcessor
{
    private readonly IMessageSerializer _serializer;

    /// <summary>Creates a marshal (serialize) processor.</summary>
    /// <param name="serializer">Serializer to use.</param>
    public MarshalProcessor(IMessageSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var body = exchange.In.Body;
        if (body is null or byte[])
            return Task.CompletedTask;

        var bytes = SerializeBody(body);
        exchange.In.Body = bytes;
        exchange.In.ContentType = _serializer.ContentType;
        exchange.In.Headers["Content-Type"] = _serializer.ContentType;
        return Task.CompletedTask;
    }

    private byte[] SerializeBody(object body)
    {
        // Use reflection to call Serialize<T> with the actual runtime type
        var method = typeof(IMessageSerializer)
            .GetMethod(nameof(IMessageSerializer.Serialize))!
            .MakeGenericMethod(body.GetType());
        return (byte[])method.Invoke(_serializer, [body])!;
    }
}

/// <summary>
/// Processor that unmarshals the exchange body from bytes to a typed object
/// using an <see cref="IMessageSerializer"/>.
/// </summary>
public sealed class UnmarshalProcessor : IProcessor
{
    private readonly IMessageSerializer _serializer;
    private readonly Type _targetType;

    /// <summary>Creates an unmarshal (deserialize) processor.</summary>
    /// <param name="serializer">Serializer to use.</param>
    /// <param name="targetType">Type to deserialize to.</param>
    public UnmarshalProcessor(IMessageSerializer serializer, Type targetType)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _targetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (exchange.In.Body is not byte[] bytes)
            return Task.CompletedTask;

        exchange.In.Body = _serializer.Deserialize(bytes, _targetType);
        return Task.CompletedTask;
    }
}
