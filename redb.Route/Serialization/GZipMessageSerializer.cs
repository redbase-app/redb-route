using System.IO.Compression;

namespace redb.Route.Serialization;

/// <summary>GZip as a data format: <c>Marshal("application/gzip")</c> compresses the body, <c>Unmarshal&lt;string&gt;("application/gzip")</c> decompresses it.</summary>
public sealed class GZipMessageSerializer : BinaryWrapperSerializer
{
    /// <summary>Content type the format is registered under by default.</summary>
    public const string DefaultContentType = "application/gzip";

    private readonly CompressionLevel _level;
    private readonly long _maxDecodedBytes;

    /// <summary>Creates the format.</summary>
    /// <param name="level">Compression level (default <see cref="CompressionLevel.Optimal"/>).</param>
    /// <param name="maxDecodedBytes">Cap on the decompressed size (default <see cref="BinaryWrapperSerializer.DefaultMaxDecodedBytes"/>, 128 MB); a body that expands past it is refused.</param>
    public GZipMessageSerializer(CompressionLevel level = CompressionLevel.Optimal, long maxDecodedBytes = DefaultMaxDecodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        _level = level;
        _maxDecodedBytes = maxDecodedBytes;
    }

    /// <inheritdoc />
    public override string ContentType => DefaultContentType;

    /// <inheritdoc />
    public override IReadOnlyCollection<string> MediaTypes => [DefaultContentType, "application/x-gzip"];

    /// <inheritdoc />
    protected override string FormatName => "GZip";

    /// <inheritdoc />
    protected override byte[] Encode(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, _level, leaveOpen: true))
            gzip.Write(payload);
        return output.ToArray();
    }

    /// <inheritdoc />
    protected override byte[] Decode(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return ReadBounded(gzip, _maxDecodedBytes, FormatName);
    }
}
