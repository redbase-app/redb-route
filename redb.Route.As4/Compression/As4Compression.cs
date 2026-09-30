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
            using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
            part.OpenRead().CopyTo(gzip);
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
                using var input = new GZipStream(part.OpenRead(), CompressionMode.Decompress, leaveOpen: true);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > maxDecompressedBytes)
                        throw new As4ProcessingException(As4ErrorCode.DecompressionFailure,
                            $"part '{part.ContentId}' inflates beyond {maxDecompressedBytes} bytes.");
                    output.Write(buffer, 0, read);
                }
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
}
