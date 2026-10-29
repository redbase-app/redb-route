using redb.Route.Configuration;

namespace redb.Route.Core;

/// <summary>
/// A stream that holds content in memory up to a threshold and spools the rest to a temporary file, then reads it back
/// from the start as often as needed — Camel's <c>StreamCache</c>. The <c>.StreamCaching()</c> step wraps a forward-only
/// body in one; a connector uses one to spool what it must not hand to the route before it is complete and checked
/// (a decrypted attachment whose tag and signature are verified only at the end).
/// <para>
/// Two phases. While writing (<see cref="Write(byte[], int, int)"/>, <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>,
/// <see cref="CacheFromSourceAsync"/>) it cannot be read; <see cref="CompleteWriting"/> rewinds it and turns it
/// read-only and seekable. The temporary file is opened with <see cref="FileOptions.DeleteOnClose"/>: disposing the
/// cache — the exchange does, when it owns the body — removes it.
/// </para>
/// </summary>
public sealed class StreamCache : Stream, IAsyncDisposable
{
    /// <summary>Default threshold: 128 KB in memory, the rest on disk.</summary>
    public const long DefaultSpoolThreshold = 128 * 1024;

    private readonly long _spoolThreshold;
    private readonly string? _tempDirectory;
    private Stream _inner;
    private bool _completed;
    private bool _disposed;
    // Serialises the reads of this stream and of its readers (OpenReader): they share one inner stream and its position.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a cache with the threshold and temporary directory of <paramref name="options"/> (defaults when <c>null</c>).</summary>
    public StreamCache(StreamCacheOptions? options = null)
        : this(options?.SpoolThreshold ?? DefaultSpoolThreshold, options?.TempDirectory)
    {
    }

    /// <summary>Creates a cache.</summary>
    /// <param name="spoolThreshold">Bytes kept in memory before the content moves to a temporary file.</param>
    /// <param name="tempDirectory">Directory of the temporary file; <c>null</c> = the system temp directory.</param>
    public StreamCache(long spoolThreshold, string? tempDirectory = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(spoolThreshold);
        _spoolThreshold = spoolThreshold;
        _tempDirectory = tempDirectory;
        _inner = new MemoryStream();
    }

    /// <summary>Whether the content went past the threshold and lives in a temporary file.</summary>
    public bool IsSpooled { get; private set; }

    /// <summary>
    /// Reads all of <paramref name="source"/> into the cache and completes writing. The source is not disposed: its
    /// owner closes it (the <c>.StreamCaching()</c> step does, since the cache replaces it as the body).
    /// </summary>
    public async Task CacheFromSourceAsync(Stream source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            await WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        CompleteWriting();
    }

    /// <summary>Ends the writing phase: the cache rewinds and becomes read-only and seekable.</summary>
    public void CompleteWriting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            return;
        _inner.Flush();
        _inner.Position = 0;
        _completed = true;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWriting();
        _inner.Write(buffer);
        if (!IsSpooled && _inner.Length > _spoolThreshold)
            Spool();
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        EnsureWriting();
        await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        if (!IsSpooled && _inner.Length > _spoolThreshold)
            await SpoolAsync(ct).ConfigureAwait(false);
    }

    private FileStream OpenSpoolFile() => new(
        Path.Combine(_tempDirectory ?? Path.GetTempPath(), $"redb-stream-{Guid.NewGuid():N}.tmp"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
        FileOptions.Asynchronous | FileOptions.DeleteOnClose);

    private void Spool()
    {
        var file = OpenSpoolFile();
        _inner.Position = 0;
        _inner.CopyTo(file);
        _inner.Dispose();
        _inner = file;
        IsSpooled = true;
    }

    private async Task SpoolAsync(CancellationToken ct)
    {
        var file = OpenSpoolFile();
        _inner.Position = 0;
        await _inner.CopyToAsync(file, ct).ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);
        _inner = file;
        IsSpooled = true;
    }

    private void EnsureWriting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new NotSupportedException("StreamCache is read-only once writing completed.");
    }

    private void EnsureReadable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_completed)
            throw new NotSupportedException("StreamCache is still being written: call CompleteWriting() before reading it.");
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed && _completed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed && _completed;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && !_completed;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set
        {
            EnsureReadable();
            _gate.Wait();
            try { _inner.Position = value; }
            finally { _gate.Release(); }
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        EnsureReadable();
        _gate.Wait();
        try { return _inner.Read(buffer); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        EnsureReadable();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureReadable();
        _gate.Wait();
        try { return _inner.Seek(offset, origin); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Opens an independent read-only view of the completed cache, positioned at the start: it has a position of its own,
    /// so several readers (and this stream) read the same content without moving each other. Disposing a reader leaves
    /// the cache open; the cache's owner disposes it.
    /// </summary>
    public Stream OpenReader()
    {
        EnsureReadable();
        return new Reader(this);
    }

    private int ReadAt(long position, Span<byte> buffer)
    {
        EnsureReadable();
        _gate.Wait();
        try
        {
            var saved = _inner.Position;
            _inner.Position = position;
            try { return _inner.Read(buffer); }
            finally { _inner.Position = saved; }
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken ct)
    {
        EnsureReadable();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = _inner.Position;
            _inner.Position = position;
            try { return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false); }
            finally { _inner.Position = saved; }
        }
        finally { _gate.Release(); }
    }

    private sealed class Reader(StreamCache cache) : Stream
    {
        private long _position;
        private bool _closed;

        public override bool CanRead => !_closed && cache.CanRead;
        public override bool CanSeek => CanRead;
        public override bool CanWrite => false;
        public override long Length => cache.Length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var read = cache.ReadAt(_position, buffer);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var read = await cache.ReadAtAsync(_position, buffer, ct).ConfigureAwait(false);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void Flush() { }

        public override void SetLength(long value) => throw new NotSupportedException("A StreamCache reader is read-only.");

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("A StreamCache reader is read-only.");

        protected override void Dispose(bool disposing)
        {
            _closed = true;
            base.Dispose(disposing);
        }
    }

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("StreamCache grows by writing only.");

    /// <inheritdoc />
    public override void Flush()
    {
        if (!_disposed)
            _inner.Flush();
    }

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
