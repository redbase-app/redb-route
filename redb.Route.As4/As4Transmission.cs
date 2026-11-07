using System.Net;
using System.Xml;

namespace redb.Route.As4;

/// <summary>
/// One signed, encrypted AS4 request as it went on the wire, kept on its exchange for redelivery. A retransmission is
/// the same message (AS4 reception awareness): Domibus answers a duplicate with the stored receipt of the first
/// transmission, whose non-repudiation digests match only the first signature, so a rebuilt message — new timestamp,
/// new signature — could never have its receipt verified. The exchange owns it and releases it when it ends
/// (<c>ExchangeResources.ReleaseWithExchange</c>); another exchange, even with the same message id, builds its own.
/// </summary>
internal sealed class As4Transmission : IAsyncDisposable
{
    /// <summary>Exchange property that holds the transmission between attempts.</summary>
    public const string Property = "redbAs4.transmission";

    public As4Transmission(string messageId, string partner, object? source, Stream body, string contentType, IReadOnlyList<XmlElement> signed)
    {
        MessageId = messageId;
        Partner = partner;
        Source = source;
        Body = body;
        ContentType = contentType;
        Signed = signed;
    }

    /// <summary>The <c>eb:MessageId</c> it carries.</summary>
    public string MessageId { get; }

    /// <summary>The agreement it was built for.</summary>
    public string Partner { get; }

    /// <summary>The exchange body it was built from; a route that replaced the body sends a new message.</summary>
    public object? Source { get; }

    /// <summary>The request body, readable and seekable (the core stream cache).</summary>
    public Stream Body { get; }

    /// <summary>The request Content-Type.</summary>
    public string ContentType { get; }

    /// <summary>The <c>ds:Reference</c> elements we signed — what the receipt's non-repudiation information must name.</summary>
    public IReadOnlyList<XmlElement> Signed { get; }

    /// <summary>Whether a redelivery of <paramref name="messageId"/> to <paramref name="partner"/> with <paramref name="source"/> is this message.</summary>
    public bool IsFor(string messageId, string partner, object? source) =>
        MessageId == messageId && Partner == partner && ReferenceEquals(Source, source);

    /// <summary>The request content; each attempt reads the body from its start and leaves it open for the next.</summary>
    public HttpContent Content()
    {
        var content = new ReplayContent(Body);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ContentType);
        content.Headers.ContentLength = Body.Length;
        return content;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Body.DisposeAsync();

    /// <summary>Content over a stream it does not own: <c>StreamContent</c> would close it with the request.</summary>
    private sealed class ReplayContent(Stream body) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            body.Position = 0;
            await body.CopyToAsync(stream).ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            body.Position = 0;
            return body.CopyToAsync(stream, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }
}
