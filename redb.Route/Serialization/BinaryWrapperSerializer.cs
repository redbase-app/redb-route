using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Serialization;

/// <summary>
/// Base for data formats that wrap bytes rather than serialize an object model (Base64, GZip, Zip).
/// <c>Marshal</c> accepts a <c>byte[]</c>, a <c>string</c> (UTF-8) or a <see cref="Stream"/>;
/// <c>Unmarshal</c> yields <c>byte[]</c>, <c>string</c>, <see cref="Stream"/> or <c>object</c> (bytes).
/// Anything else is an error that names the format, not a <c>NullReferenceException</c> later.
/// </summary>
public abstract class BinaryWrapperSerializer : IMessageSerializer
{
    /// <inheritdoc />
    public abstract string ContentType { get; }

    /// <inheritdoc />
    public virtual IReadOnlyCollection<string> MediaTypes => [ContentType];

    /// <inheritdoc />
    public bool WrapsBytes => true;

    /// <summary>
    /// Default cap on the unwrapped size, 128 MB. A body that expands past the cap is refused, so a small
    /// compressed message cannot exhaust the process memory; raise it per format instance when a bigger
    /// payload is expected (<c>new GZipMessageSerializer(maxDecodedBytes: …)</c>).
    /// </summary>
    public const long DefaultMaxDecodedBytes = 128L * 1024 * 1024;

    /// <summary>Wraps the payload (encode / compress).</summary>
    protected abstract byte[] Encode(byte[] payload);

    /// <summary>Unwraps the data (decode / decompress).</summary>
    protected abstract byte[] Decode(byte[] data);

    /// <summary>Short format name for error messages (<c>GZip</c>).</summary>
    protected abstract string FormatName { get; }

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) => Encode(ToBytes(value));

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data) => (T?)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(type);

        byte[] raw;
        try { raw = Decode(data); }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException)
        {
            throw new InvalidOperationException($"{FormatName} data format: the body is not valid {FormatName} data ({ex.Message}).", ex);
        }

        if (type == typeof(byte[]) || type == typeof(object)) return raw;
        if (type == typeof(string)) return Encoding.UTF8.GetString(raw);
        if (type == typeof(Stream) || type == typeof(MemoryStream)) return new MemoryStream(raw, writable: false);
        if (type == typeof(ReadOnlyMemory<byte>)) return new ReadOnlyMemory<byte>(raw);

        throw new InvalidOperationException(
            $"{FormatName} data format wraps bytes: unmarshal to byte[], string or Stream, not {type.Name}.");
    }

    private byte[] ToBytes(object? value) => value switch
    {
        null => [],
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        Stream stream => ReadAll(stream),
        _ => throw new InvalidOperationException(
            $"{FormatName} data format wraps bytes: the body must be byte[], string or Stream, not {value.GetType().Name}."),
    };

    /// <summary>
    /// Copies the decoded <paramref name="source"/> into a buffer, refusing to grow past
    /// <paramref name="limit"/> bytes. Thrown as <see cref="InvalidOperationException"/> on purpose: it is
    /// not "invalid data" (the stream may be perfectly valid), it is a body the format refuses to unwrap.
    /// </summary>
    protected static byte[] ReadBounded(Stream source, long limit, string formatName)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
                throw new InvalidOperationException(
                    $"{formatName} data format: the decoded body exceeds the limit of {limit} bytes; raise maxDecodedBytes on the {formatName} format if a body this large is expected.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
