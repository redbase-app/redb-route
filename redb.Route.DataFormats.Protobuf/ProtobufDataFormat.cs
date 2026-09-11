using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;
using redb.Route.Abstractions;
using ProtoMessage = Google.Protobuf.IMessage;

namespace redb.Route.DataFormats.Protobuf;

/// <summary>
/// Protocol Buffers <see cref="IMessageSerializer"/> (Google.Protobuf). Marshals any <see cref="IMessage"/>;
/// unmarshals to a generated message type through its static <c>Parser</c>. Optional Confluent
/// Schema Registry framing (<see cref="ProtobufDataFormatOptions.ConfluentWireFormat"/>).
/// </summary>
public sealed class ProtobufDataFormat : IMessageSerializer
{
    /// <summary>Content type the format is registered under.</summary>
    public const string DefaultContentType = "application/x-protobuf";

    private static readonly ConcurrentDictionary<Type, MessageParser> Parsers = new();
    private readonly ProtobufDataFormatOptions _options;

    /// <summary>Creates the format with the given options (defaults when <c>null</c>).</summary>
    public ProtobufDataFormat(ProtobufDataFormatOptions? options = null) => _options = options ?? new ProtobufDataFormatOptions();

    /// <summary>The options in effect.</summary>
    public ProtobufDataFormatOptions Options => _options;

    /// <inheritdoc />
    public string ContentType => DefaultContentType;

    /// <inheritdoc />
    public IReadOnlyCollection<string> MediaTypes => [DefaultContentType, "application/protobuf", "application/vnd.google.protobuf"];

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        switch (value)
        {
            case null: return [];
            case byte[] bytes: return bytes;
            case ProtoMessage message:
                var payload = message.ToByteArray();
                return _options.ConfluentWireFormat ? Frame(payload) : payload;
            default:
                throw new InvalidOperationException(
                    $"Protobuf data format: the body must be a Google.Protobuf IMessage, not {value.GetType().Name}.");
        }
    }

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data) => (T?)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(type);

        if (type == typeof(byte[])) return data;
        if (!typeof(ProtoMessage).IsAssignableFrom(type))
            throw new InvalidOperationException(
                $"Protobuf data format: unmarshal target must be a Google.Protobuf IMessage type, not {type.Name}.");

        var payload = _options.ConfluentWireFormat ? Unframe(data) : data;
        try
        {
            return ParserFor(type).ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidOperationException($"Protobuf data format: the body is not a valid {type.Name} ({ex.Message}).", ex);
        }
    }

    private static MessageParser ParserFor(Type type) => Parsers.GetOrAdd(type, static t =>
        t.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as MessageParser
        ?? throw new InvalidOperationException($"Protobuf data format: {t.Name} has no static Parser property; is it a protoc-generated message?"));

    /// <summary>Confluent framing: <c>0x00</c>, schema id big-endian, message index list <c>[0]</c> encoded as a single zero byte.</summary>
    private byte[] Frame(byte[] payload)
    {
        var framed = new byte[6 + payload.Length];
        framed[0] = 0;
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), _options.SchemaId);
        framed[5] = 0;
        payload.CopyTo(framed, 6);
        return framed;
    }

    private static ReadOnlySpan<byte> Unframe(byte[] data)
    {
        if (data.Length < 6 || data[0] != 0)
            throw new InvalidOperationException("Protobuf data format: the body is not in Confluent wire format (magic byte 0 + schema id expected).");

        var offset = 5;
        var count = ReadVarint(data, ref offset);   // message-index list length; a single 0 byte means [0]
        for (var i = 0L; i < count; i++)
            ReadVarint(data, ref offset);
        return data.AsSpan(offset);
    }

    private static long ReadVarint(byte[] data, ref int offset)
    {
        long result = 0;
        var shift = 0;
        while (offset < data.Length)
        {
            var b = data[offset++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        throw new InvalidOperationException("Protobuf data format: truncated Confluent wire-format header.");
    }
}
