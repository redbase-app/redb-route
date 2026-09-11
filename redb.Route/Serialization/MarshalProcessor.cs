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
        if (body is null)
            return Task.CompletedTask;
        // For an object-model format (JSON, XML, Avro, ...) a byte[] body is an already-marshalled result
        // and passes through; for a byte wrapper (GZip, Zip, Base64) the bytes are the payload itself.
        if (body is byte[] && !_serializer.WrapsBytes)
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
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // Text formats (CSV, YAML, JSON) usually arrive as a string; a Stream is common after an
        // HTTP or file consumer. All three are the same bytes to a serializer.
        var bytes = exchange.In.Body switch
        {
            byte[] b => b,
            string s => System.Text.Encoding.UTF8.GetBytes(s),
            Stream stream => await ReadAllAsync(stream, ct).ConfigureAwait(false),
            _ => null,
        };
        if (bytes is null)
            return;

        exchange.In.Body = _serializer.Deserialize(bytes, _targetType);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        // The buffer shortcut only when the stream is at its start; a positioned stream (an earlier step
        // consumed framing) is read from its position like any other, never from byte zero.
        if (stream is MemoryStream ms && ms.Position == 0 && ms.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Count == ms.Length)
            return segment.Array!.Length == segment.Count ? segment.Array : segment.ToArray();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
