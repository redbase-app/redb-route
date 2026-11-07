using redb.Route.Core;

namespace redb.Route.Abstractions;

/// <summary>
/// The content of an <see cref="Attachment"/>: a source that opens a new stream from the start on every read. Reading
/// more than once is required, not optional: Multicast branches, redelivery and a resend over a fresh connection all
/// read the same attachment again, so a forward-only stream is not accepted as a source.
/// </summary>
/// <remarks>
/// A source that holds a resource (a <see cref="StreamCache"/>, a file to delete) implements
/// <see cref="IAsyncDisposable"/>; the exchange that carries the message releases it like a disposable body.
/// </remarks>
public abstract class AttachmentContent
{
    /// <summary>Length of the content in bytes.</summary>
    public abstract long Length { get; }

    /// <summary>Opens a new read-only stream positioned at the start of the content.</summary>
    public abstract Stream OpenRead();

    /// <summary>Opens a new read-only stream positioned at the start of the content.</summary>
    public virtual ValueTask<Stream> OpenReadAsync(CancellationToken ct = default) => ValueTask.FromResult(OpenRead());

    /// <summary>Reads the whole content into a new array.</summary>
    public virtual byte[] ReadAllBytes()
    {
        using var stream = OpenRead();
        using var buffer = new MemoryStream(Length > int.MaxValue ? 0 : (int)Length);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Reads the whole content into a new array.</summary>
    public virtual async ValueTask<byte[]> ReadAllBytesAsync(CancellationToken ct = default)
    {
        var stream = await OpenReadAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream(Length > int.MaxValue ? 0 : (int)Length);
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>
    /// An isolated copy for a checkpoint snapshot: the content is read into memory, so the copy outlives the source and
    /// later changes to it.
    /// </summary>
    public virtual AttachmentContent DeepCopy() => new BytesContent(ReadAllBytes());

    /// <summary>Content held in memory. The array is shared, not copied, like a <c>byte[]</c> body.</summary>
    public static AttachmentContent FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new BytesContent(bytes);
    }

    /// <summary>Content of a file, read from disk on every open.</summary>
    /// <param name="path">Path of an existing file.</param>
    /// <param name="deleteWithExchange"><c>true</c> to delete the file when the exchange carrying the message is released.</param>
    public static AttachmentContent FromFile(string path, bool deleteWithExchange = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Attachment file does not exist.", full);
        return deleteWithExchange ? new OwnedFileContent(full) : new FileContent(full);
    }

    /// <summary>
    /// Content of a completed <see cref="StreamCache"/>; every open gets an independent reader. The cache is disposed
    /// with the exchange carrying the message.
    /// </summary>
    public static AttachmentContent FromStreamCache(StreamCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (!cache.CanRead)
            throw new ArgumentException("The StreamCache must be completed (CompleteWriting) before it becomes attachment content.", nameof(cache));
        return new StreamCacheContent(cache);
    }

    private sealed class BytesContent(byte[] bytes) : AttachmentContent
    {
        public override long Length => bytes.LongLength;
        public override Stream OpenRead() => new MemoryStream(bytes, writable: false);
        public override byte[] ReadAllBytes() => (byte[])bytes.Clone();
        public override ValueTask<byte[]> ReadAllBytesAsync(CancellationToken ct = default) => ValueTask.FromResult(ReadAllBytes());
        public override AttachmentContent DeepCopy() => new BytesContent((byte[])bytes.Clone());
    }

    private class FileContent(string path) : AttachmentContent
    {
        protected string FilePath => path;
        public override long Length => new FileInfo(path).Length;

        public override Stream OpenRead() =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.SequentialScan);

        public override ValueTask<Stream> OpenReadAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    private sealed class OwnedFileContent(string path) : FileContent(path), IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            File.Delete(FilePath);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StreamCacheContent(StreamCache cache) : AttachmentContent, IAsyncDisposable
    {
        public override long Length => cache.Length;
        public override Stream OpenRead() => cache.OpenReader();
        public ValueTask DisposeAsync() => cache.DisposeAsync();
    }
}
