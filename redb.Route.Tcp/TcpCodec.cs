using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace redb.Route.Tcp;

/// <summary>
/// Codec for reading and writing framed TCP messages.
/// Supports Raw, TextLine, and LengthPrefixed framing modes.
/// Thread-safe: stateless, all state is in the stream.
/// </summary>
internal static class TcpCodec
{
    /// <summary>
    /// Reads one framed message from the stream.
    /// Returns null if the connection is closed (0 bytes read).
    /// </summary>
    public static async Task<byte[]?> ReadMessageAsync(
        Stream stream, TcpFraming framing, string delimiter, int bufferSize, CancellationToken ct)
    {
        return framing switch
        {
            TcpFraming.TextLine => await ReadTextLineAsync(stream, delimiter, bufferSize, ct).ConfigureAwait(false),
            TcpFraming.LengthPrefixed => await ReadLengthPrefixedAsync(stream, ct).ConfigureAwait(false),
            _ => await ReadRawAsync(stream, bufferSize, ct).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// Writes one framed message to the stream.
    /// </summary>
    public static async Task WriteMessageAsync(
        Stream stream, byte[] data, TcpFraming framing, string delimiter, Encoding encoding, CancellationToken ct)
    {
        switch (framing)
        {
            case TcpFraming.TextLine:
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
                var delimBytes = encoding.GetBytes(delimiter);
                await stream.WriteAsync(delimBytes, ct).ConfigureAwait(false);
                break;

            case TcpFraming.LengthPrefixed:
                var header = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
                await stream.WriteAsync(header, ct).ConfigureAwait(false);
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
                break;

            default: // Raw
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
                break;
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads bytes until the delimiter is encountered.</summary>
    private static async Task<byte[]?> ReadTextLineAsync(
        Stream stream, string delimiter, int bufferSize, CancellationToken ct)
    {
        // We read byte-by-byte into a buffer, scanning for the delimiter.
        // For high-throughput, a PipeReader would be better, but this is simple and correct.
        var buffer = new MemoryStream(256);
        var delimBytes = Encoding.UTF8.GetBytes(delimiter);
        var matchPos = 0;
        var singleByte = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(singleByte, ct).ConfigureAwait(false);
            if (read == 0)
                return buffer.Position > 0 ? buffer.ToArray() : null;

            buffer.WriteByte(singleByte[0]);

            if (singleByte[0] == delimBytes[matchPos])
            {
                matchPos++;
                if (matchPos == delimBytes.Length)
                {
                    // Remove delimiter from the result
                    var result = buffer.ToArray();
                    return result.AsSpan(0, result.Length - delimBytes.Length).ToArray();
                }
            }
            else
            {
                matchPos = singleByte[0] == delimBytes[0] ? 1 : 0;
            }
        }
    }

    /// <summary>Reads a 4-byte big-endian length header, then exactly that many bytes.</summary>
    private static async Task<byte[]?> ReadLengthPrefixedAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var headerRead = await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (!headerRead) return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0) return [];

        var payload = new byte[length];
        var payloadRead = await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        if (!payloadRead) return null;

        return payload;
    }

    /// <summary>Reads a single chunk of available data (raw mode).</summary>
    private static async Task<byte[]?> ReadRawAsync(Stream stream, int bufferSize, CancellationToken ct)
    {
        var buffer = new byte[bufferSize];
        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read == 0) return null;

        return buffer.AsSpan(0, read).ToArray();
    }

    /// <summary>Reads exactly buffer.Length bytes or returns false on EOF.</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
