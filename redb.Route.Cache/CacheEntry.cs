using System.Text.Json.Nodes;
using redb.Route.Abstractions;

namespace redb.Route.Cache;

/// <summary>What the cache stores for one key: the body, its content type and optionally the headers.</summary>
public sealed class CacheEntry
{
    /// <summary>Cached body (a CLR object in the in-process cache; restored to its type from a distributed cache when possible).</summary>
    public object? Body { get; init; }

    /// <summary>Content type of the body.</summary>
    public string? ContentType { get; init; }

    /// <summary>Headers captured with the body (only when the node asked for them).</summary>
    public IReadOnlyDictionary<string, object?>? Headers { get; init; }

    /// <summary>
    /// The miss left its result in <c>exchange.Out</c> (a request-reply producer such as HTTP replies
    /// there), so a hit restores it there too — a consumer that answers only when <c>Out</c> is present
    /// must see a hit exactly as it saw the miss.
    /// </summary>
    public bool FromOut { get; init; }

    /// <summary>
    /// Captures the message. A <see cref="Stream"/> body can be read once, so its bytes are kept and the
    /// live message continues with those bytes; a <see cref="JsonNode"/> has one parent and is mutable,
    /// so the cache keeps its own copy. Any other body is kept by reference — text and byte arrays are
    /// treated as immutable, and a mutable object (a POCO, a list) is shared by every hit: do not mutate
    /// what a cache scope handed you, or clone it first.
    /// </summary>
    public static CacheEntry From(IMessage message, bool includeHeaders, bool fromOut = false)
    {
        var body = message.Body;
        switch (body)
        {
            case Stream stream:
                body = ReadToEnd(stream);
                message.Body = body;
                break;
            case JsonNode node:
                body = node.DeepClone();
                break;
        }

        return new()
        {
            Body = body,
            ContentType = message.ContentType,
            Headers = includeHeaders ? new Dictionary<string, object?>(message.Headers, StringComparer.OrdinalIgnoreCase) : null,
            FromOut = fromOut,
        };
    }

    private static byte[] ReadToEnd(Stream stream)
    {
        using (stream)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    /// <summary>
    /// Writes the entry into the message. Cached headers overwrite the current ones: that is what the
    /// inner steps did on the miss, and a hit must leave the message the way the miss did.
    /// </summary>
    public void ApplyTo(IMessage message)
    {
        message.Body = Body is JsonNode node ? node.DeepClone() : Body;
        if (ContentType is not null) message.ContentType = ContentType;
        if (Headers is null) return;
        foreach (var (key, value) in Headers)
            message.Headers[key] = value;
    }

    /// <summary>Writes the entry where the miss left it: a fresh <c>Out</c> when the inner steps replied there, otherwise <c>In</c>.</summary>
    public void ApplyTo(IExchange exchange)
    {
        if (!FromOut)
        {
            ApplyTo(exchange.In);
            return;
        }

        var reply = new redb.Route.Core.Message(Body);
        ApplyTo(reply);
        exchange.Out = reply;
    }
}
