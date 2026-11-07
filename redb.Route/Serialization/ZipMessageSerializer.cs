using System.IO.Compression;

namespace redb.Route.Serialization;

/// <summary>
/// Zip as a data format: <c>Marshal("application/zip")</c> packs the body into a single-entry archive
/// (entry name from the constructor, default <c>body</c>); <c>Unmarshal</c> reads the first entry.
/// </summary>
public sealed class ZipMessageSerializer : BinaryWrapperSerializer
{
    /// <summary>Content type the format is registered under by default.</summary>
    public const string DefaultContentType = "application/zip";

    private readonly string _entryName;
    private readonly CompressionLevel _level;
    private readonly long _maxDecodedBytes;

    /// <summary>Creates the format.</summary>
    /// <param name="entryName">Name of the single archive entry written by <c>Marshal</c>.</param>
    /// <param name="level">Compression level (default <see cref="CompressionLevel.Optimal"/>).</param>
    /// <param name="maxDecodedBytes">Cap on the decompressed size (default <see cref="BinaryWrapperSerializer.DefaultMaxDecodedBytes"/>, 128 MB); an entry that expands past it is refused.</param>
    public ZipMessageSerializer(string entryName = "body", CompressionLevel level = CompressionLevel.Optimal, long maxDecodedBytes = DefaultMaxDecodedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        _entryName = entryName;
        _level = level;
        _maxDecodedBytes = maxDecodedBytes;
    }

    /// <inheritdoc />
    public override string ContentType => DefaultContentType;

    /// <inheritdoc />
    public override IReadOnlyCollection<string> MediaTypes => [DefaultContentType, "application/x-zip-compressed"];

    /// <inheritdoc />
    protected override string FormatName => "Zip";

    /// <inheritdoc />
    protected override byte[] Encode(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry(_entryName, _level).Open();
            entry.Write(payload);
        }
        return output.ToArray();
    }

    /// <inheritdoc />
    protected override byte[] Decode(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        // Directory entries (empty Name) carry no data; the format reads exactly one file entry — a
        // multi-entry archive is refused rather than silently reduced to whichever entry came first.
        var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (files.Count == 0)
            throw new InvalidDataException("the archive has no file entries");
        if (files.Count > 1)
            throw new InvalidDataException($"the archive has {files.Count} entries; Unmarshal reads a single-entry archive");
        using var stream = files[0].Open();
        return ReadBounded(stream, _maxDecodedBytes, FormatName);
    }
}
