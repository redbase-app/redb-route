namespace redb.Route.As2.Crypto;

/// <summary>
/// Writes through to a stream and fails past a limit, so an unwrapping step that inflates its input (decompression)
/// stops at the bound instead of filling the spool.
/// </summary>
internal sealed class BoundedWriteStream(Stream inner, long limit, string limitName) : Stream
{
    private long _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Count(count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Count(buffer.Length);
        inner.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        Count(1);
        inner.WriteByte(value);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Count(buffer.Length);
        await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private void Count(int count)
    {
        _written += count;
        if (_written > limit)
            throw new InvalidDataException($"the content inflates past {limitName} {limit} bytes.");
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
