using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using Chr.Avro.Abstract;
using Chr.Avro.Representation;
using Chr.Avro.Serialization;
using redb.Route.Abstractions;

namespace redb.Route.DataFormats.Avro;

/// <summary>
/// Apache Avro binary <see cref="IMessageSerializer"/> (Chr.Avro). The schema comes from
/// <see cref="AvroDataFormatOptions.Schema"/> or is built from the CLR type; serializer and deserializer
/// delegates are built once per type and cached. Optional Confluent Schema Registry framing.
/// </summary>
public sealed class AvroDataFormat : IMessageSerializer
{
    /// <summary>Content type the format is registered under.</summary>
    public const string DefaultContentType = "application/avro";

    private readonly AvroDataFormatOptions _options;
    private readonly ConcurrentDictionary<Type, Schema> _schemas = new();
    private readonly ConcurrentDictionary<Type, Delegate> _serializers = new();
    private readonly ConcurrentDictionary<Type, Delegate> _deserializers = new();
    private readonly Lazy<Schema?> _explicitSchema;

    /// <summary>Creates the format with the given options (defaults when <c>null</c>).</summary>
    public AvroDataFormat(AvroDataFormatOptions? options = null)
    {
        _options = options ?? new AvroDataFormatOptions();
        _explicitSchema = new Lazy<Schema?>(() => _options.Schema is null ? null : new JsonSchemaReader().Read(_options.Schema));
    }

    /// <summary>The options in effect.</summary>
    public AvroDataFormatOptions Options => _options;

    /// <inheritdoc />
    public string ContentType => DefaultContentType;

    /// <inheritdoc />
    public IReadOnlyCollection<string> MediaTypes => [DefaultContentType, "avro/binary", "application/vnd.apache.avro+binary"];

    /// <summary>The schema used for <paramref name="type"/> (explicit or built from the type).</summary>
    public Schema SchemaFor(Type type)
        => _explicitSchema.Value ?? _schemas.GetOrAdd(type, static t => new SchemaBuilder().BuildSchema(t));

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        switch (value)
        {
            case null: return [];
            case byte[] bytes: return bytes;
        }

        try
        {
            var serialize = (BinarySerializer<T>)_serializers.GetOrAdd(typeof(T),
                t => new BinarySerializerBuilder().BuildDelegate<T>(SchemaFor(t)));
            using var stream = new MemoryStream();
            using (var writer = new Chr.Avro.Serialization.BinaryWriter(stream))
                serialize(value, writer);
            var payload = stream.ToArray();
            return _options.ConfluentWireFormat ? Frame(payload) : payload;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Avro data format: cannot marshal {typeof(T).Name} ({ex.Message}).", ex);
        }
    }

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (typeof(T) == typeof(byte[])) return (T)(object)data;

        var payload = _options.ConfluentWireFormat ? Unframe(data) : data.AsMemory();
        try
        {
            var deserialize = (BinaryDeserializer<T>)_deserializers.GetOrAdd(typeof(T),
                t => new BinaryDeserializerBuilder().BuildDelegate<T>(SchemaFor(t)));
            var reader = new Chr.Avro.Serialization.BinaryReader(payload.Span);
            return deserialize(ref reader);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Avro data format: the body is not a valid {typeof(T).Name} ({ex.Message}).", ex);
        }
    }

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var method = typeof(AvroDataFormat).GetMethod(nameof(Deserialize), BindingFlags.Public | BindingFlags.Instance, [typeof(byte[])])!;
        try
        {
            return method.MakeGenericMethod(type).Invoke(this, [data]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private byte[] Frame(byte[] payload)
    {
        var framed = new byte[5 + payload.Length];
        framed[0] = 0;
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), _options.SchemaId);
        payload.CopyTo(framed, 5);
        return framed;
    }

    private static ReadOnlyMemory<byte> Unframe(byte[] data)
    {
        if (data.Length < 5 || data[0] != 0)
            throw new InvalidOperationException("Avro data format: the body is not in Confluent wire format (magic byte 0 + schema id expected).");
        return data.AsMemory(5);
    }
}
