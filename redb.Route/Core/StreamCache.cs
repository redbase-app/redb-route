namespace redb.Route.Core;

/// <summary>
/// A seekable stream wrapper that caches content from a forward-only source stream.
/// Below the spool threshold, data is stored in memory. Above it, data is spooled to a temporary file.
/// After caching is complete, the source stream is closed and the cache can be rewound (Position = 0).
/// </summary>
internal sealed class StreamCache : Stream, IAsyncDisposable
{
    private readonly long _spoolThreshold;
    private readonly string? _tempDirectory;
    private Stream _inner;
    private bool _spooled;
    private string? _tempFilePath;
    private bool _disposed;

    /// <summary>Creates a new stream cache.</summary>
    /// <param name="spoolThreshold">Maximum bytes to keep in memory before spooling to disk.</param>
    /// <param name="tempDirectory">Directory for temporary files (null = system temp).</param>
    internal StreamCache(long spoolThreshold = 128 * 1024, string? tempDirectory = null)
    {
        _spoolThreshold = spoolThreshold;
        _tempDirectory = tempDirectory;
        _inner = new MemoryStream();
    }

    /// <summary>Reads the entire source stream into this cache, then resets position to 0.</summary>
    /// <param name="source">The source stream to cache (will NOT be disposed by this method).</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task CacheFromSourceAsync(Stream source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var buffer = new byte[8192];
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await _inner.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

            if (!_spooled && _inner.Length > _spoolThreshold)
                await SpoolToDiskAsync(ct).ConfigureAwait(false);
        }

        _inner.Position = 0;
    }

    private async Task SpoolToDiskAsync(CancellationToken ct)
    {
        var dir = _tempDirectory ?? Path.GetTempPath();
        _tempFilePath = Path.Combine(dir, $"redb-stream-{Guid.NewGuid():N}.tmp");
        var fileStream = new FileStream(_tempFilePath, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        // Copy already-buffered memory data to the file
        _inner.Position = 0;
        await _inner.CopyToAsync(fileStream, ct).ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);

        _inner = fileStream;
        _spooled = true;
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _inner.ReadAsync(buffer, offset, count, ct);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        _inner.ReadAsync(buffer, ct);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        _inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("StreamCache is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("StreamCache is read-only.");

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _inner.DisposeAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }
}
