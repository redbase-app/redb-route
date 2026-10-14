using System.IO.Compression;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.Configuration;

namespace redb.Route.As4.Compression;

/// <summary>
/// AS4 compression (profile §3.1, eDelivery AS4 1.16): an attachment is gzipped before it is signed and
/// encrypted; <c>eb:PartProperties</c> carry <c>CompressionType=application/gzip</c> and the original
/// <c>MimeType</c>. The receiver decompresses after decryption and signature verification.
/// </summary>
internal static class As4Compression
{
    /// <summary>The only compression type AS4 defines.</summary>
    public const string Gzip = "application/gzip";

    /// <summary>Part property naming the compression.</summary>
    public const string CompressionTypeProperty = "CompressionType";

    /// <summary>Part property naming the original media type.</summary>
    public const string MimeTypeProperty = "MimeType";

    /// <summary>
    /// Gzips <paramref name="part"/> in place and returns the part properties that describe it: the original
    /// media type and the compression type.
    /// </summary>
    public static IReadOnlyList<Property> Compress(SwaPart part, StreamCacheOptions spool)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(part);
        var original = part.ContentType;

        part.Replace(As4Spool.Run(spool, output =>
        {
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                part.OpenRead().CopyTo(gzip);
            // From .NET 9 on GZipStream writes nothing at all for empty content, and zero bytes are not a gzip stream:
            // a receiver refuses them (java.util.zip: EOFException). An empty payload is the empty member.
            if (output.Length == 0)
                output.Write(EmptyGzip);
        }));
        part.ContentType = Gzip;
        return [new Property(MimeTypeProperty, original, null), new Property(CompressionTypeProperty, Gzip, null)];
    }

    /// <summary>
    /// Decompresses <paramref name="part"/> in place when <paramref name="partInfo"/> says it is compressed, and
    /// gives it the original media type. More than <paramref name="maxDecompressedBytes"/> of output — the shape of
    /// a compression bomb — and anything that does not inflate are <see cref="As4ErrorCode.DecompressionFailure"/>;
    /// a compression type other than gzip, or one without <c>MimeType</c>, is <see cref="As4ErrorCode.ValueInconsistent"/>.
    /// Returns whether the part was compressed.
    /// </summary>
    public static bool Decompress(SwaPart part, PartInfo partInfo, long maxDecompressedBytes, StreamCacheOptions spool)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(partInfo);

        var compression = partInfo.PropertyValue(CompressionTypeProperty);
        if (compression is null)
            return false;
        if (compression != Gzip)
            throw new As4ProcessingException(As4ErrorCode.ValueInconsistent,
                $"part '{part.ContentId}' names compression '{compression}'; AS4 defines only {Gzip}.");
        var mimeType = partInfo.PropertyValue(MimeTypeProperty)
            ?? throw new As4ProcessingException(As4ErrorCode.ValueInconsistent,
                $"compressed part '{part.ContentId}' does not name its original MimeType.");

        Stream inflated;
        try
        {
            inflated = As4Spool.Run(spool, output =>
            {
                var compressed = part.OpenRead();
                using var input = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
                var buffer = new byte[81920];
                long total = 0;
                var crc = 0xFFFFFFFFu;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > maxDecompressedBytes)
                        throw new As4ProcessingException(As4ErrorCode.DecompressionFailure,
                            $"part '{part.ContentId}' inflates beyond {maxDecompressedBytes} bytes.");
                    crc = Crc32.Append(crc, buffer.AsSpan(0, read));
                    output.Write(buffer, 0, read);
                }

                // GZipStream ends quietly when its input stops in the middle of the stream: a truncated payload would
                // inflate to a part of the document and be acknowledged. Whether everything arrived is in the gzip
                // trailers (RFC 1952 §2.3.1: CRC-32 and length of the content). A stream of one member, which is what
                // AS4 senders write, is settled by its last eight bytes; anything else is walked member by member.
                if (!TrailerMatches(compressed, ~crc, total) && !GzipMembers.AreWhole(compressed, total))
                    throw new As4ProcessingException(As4ErrorCode.DecompressionFailure,
                        $"part '{part.ContentId}' is truncated or damaged: its gzip trailer does not match the inflated content.");
            });
        }
        catch (InvalidDataException e)
        {
            throw new As4ProcessingException(As4ErrorCode.DecompressionFailure, $"part '{part.ContentId}' is not valid gzip.", e);
        }

        part.Replace(inflated);
        part.ContentType = mimeType;
        return true;
    }

    /// <summary>
    /// Whether the last eight bytes of <paramref name="compressed"/> are the gzip trailer of content with this CRC-32
    /// and length: both little-endian, the length modulo 2^32. True only for a whole stream of one member: the trailer
    /// of a stream of several members describes its last one alone, and <see cref="GzipMembers"/> decides for those.
    /// </summary>
    private static bool TrailerMatches(Stream compressed, uint crc, long length)
    {
        const int headerAndTrailer = 10 + 8;
        if (compressed.Length < headerAndTrailer)
            return false;
        Span<byte> trailer = stackalloc byte[8];
        compressed.Position = compressed.Length - trailer.Length;
        compressed.ReadExactly(trailer);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(trailer) == crc
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) == unchecked((uint)length);
    }

    /// <summary>
    /// A gzip stream of no content (RFC 1952): the ten-byte header, an empty final deflate block, CRC-32 and length zero.
    /// </summary>
    private static ReadOnlySpan<byte> EmptyGzip =>
        [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
}
