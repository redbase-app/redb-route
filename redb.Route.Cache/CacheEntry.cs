using System.Text.Json.Nodes;
using redb.Route.Abstractions;

namespace redb.Route.Cache;

/// <summary>
/// What the cache stores for one key: the body, its content type and the headers the entry may carry.
/// A hit hands the entry to another exchange, so the headers are what the miss produced (the scope) or
/// what a <c>put</c> named, never what an exchange came in with and never a credential
/// (<see cref="CacheHeaderPolicy"/>).
/// </summary>
public sealed class CacheEntry
{
    /// <summary>Cached body (a CLR object in the in-process cache; restored to its type from a distributed cache when possible).</summary>
    public object? Body { get; init; }

    /// <summary>Content type of the body.</summary>
    public string? ContentType { get; init; }

    /// <summary>Headers of the result message: set on <c>In</c> on a hit, or on the reply when <see cref="FromOut"/>.</summary>
    public IReadOnlyDictionary<string, object?>? Headers { get; init; }

    /// <summary>
    /// The miss left its result in <c>exchange.Out</c> (a request-reply producer such as HTTP replies
    /// there), so a hit restores it there too — a consumer that answers only when <c>Out</c> is present
    /// must see a hit exactly as it saw the miss.
    /// </summary>
    public bool FromOut { get; init; }

    /// <summary>With <see cref="FromOut"/>: the headers the inner steps produced on <c>In</c> while replying in <c>Out</c>; a hit sets them on <c>In</c>.</summary>
    public IReadOnlyDictionary<string, object?>? InHeaders { get; init; }

    /// <summary>Names of the headers the inner steps removed from <c>In</c>; a hit removes them as well.</summary>
    public IReadOnlyCollection<string>? RemovedHeaders { get; init; }

    /// <summary>
    /// Captures the message with its headers as they are, credentials excluded. The scope and the
    /// component keep less: the scope the headers the miss produced (<see cref="FromResult"/>), the
    /// component the ones a <c>put</c> named.
    /// </summary>
    public static CacheEntry From(IMessage message, bool includeHeaders, bool fromOut = false)
    {
        Dictionary<string, object?>? headers = null;
        if (includeHeaders)
        {
            headers = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in message.Headers)
                if (!CacheHeaderPolicy.IsCredential(name))
                    headers[name] = value;
        }

        return new()
        {
            Body = Capture(message),
            ContentType = message.ContentType,
            Headers = headers,
            FromOut = fromOut,
        };
    }

    /// <summary>
    /// Captures what the inner steps of a scope produced: the result message (<c>Out</c> when they
    /// replied there, else <c>In</c>) and, with <paramref name="before"/>, the headers they added,
    /// changed or removed, compared by instance with the ones the exchange came in with. A header that
    /// arrived with the request is never stored; a reply that copied the request's headers keeps only
    /// its own.
    /// </summary>
    internal static CacheEntry FromResult(IExchange exchange, IReadOnlyDictionary<string, object?>? before)
    {
        var fromOut = exchange.Out is not null;
        var result = fromOut ? exchange.Out! : exchange.In;
        var body = Capture(result);
        if (before is null)
            return new() { Body = body, ContentType = result.ContentType, FromOut = fromOut };

        var produced = CacheHeaderPolicy.Produced(exchange.In, before);
        var removed = CacheHeaderPolicy.Removed(exchange.In, before);
        return fromOut
            ? new()
            {
                Body = body,
                ContentType = result.ContentType,
                FromOut = true,
                Headers = CacheHeaderPolicy.Produced(exchange.Out!, before),
                InHeaders = produced,
                RemovedHeaders = removed,
            }
            : new()
            {
                Body = body,
                ContentType = result.ContentType,
                Headers = produced,
                RemovedHeaders = removed,
            };
    }

    /// <summary>Captures the message for a <c>put</c>: the body, and of the headers only the ones named.</summary>
    internal static CacheEntry FromMessage(IMessage message, IReadOnlyCollection<string>? headerNames)
    {
        Dictionary<string, object?>? headers = null;
        if (headerNames is { Count: > 0 })
        {
            headers = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in headerNames)
                if (message.Headers.TryGetValue(name, out var value))
                    headers[name] = value;
        }

        return new() { Body = Capture(message), ContentType = message.ContentType, Headers = headers };
    }

    /// <summary>
    /// The body as the cache keeps it. A <see cref="Stream"/> body can be read once, so its bytes are
    /// kept and the live message continues with those bytes; a <see cref="JsonNode"/> has one parent and
    /// is mutable, so the cache keeps its own copy. Any other body is kept by reference — text and byte
    /// arrays are treated as immutable, and a mutable object (a POCO, a list) is shared by every hit: do
    /// not mutate what a cache scope handed you, or clone it first.
    /// </summary>
    private static object? Capture(IMessage message)
    {
        switch (message.Body)
        {
            case Stream stream:
                var bytes = ReadToEnd(stream);
                message.Body = bytes;
                return bytes;
            case JsonNode node:
                return node.DeepClone();
            case var body:
                return body;
        }
    }

    internal static byte[] ReadToEnd(Stream stream)
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

    /// <summary>
    /// Writes the entry where the miss left it: a fresh <c>Out</c> when the inner steps replied there,
    /// otherwise <c>In</c>. What the steps did to the headers of <c>In</c> is done again either way.
    /// </summary>
    public void ApplyTo(IExchange exchange)
    {
        if (!FromOut)
        {
            ApplyTo(exchange.In);
            RemoveFrom(exchange.In);
            return;
        }

        if (InHeaders is not null)
            foreach (var (key, value) in InHeaders)
                exchange.In.Headers[key] = value;
        RemoveFrom(exchange.In);

        var reply = new redb.Route.Core.Message(Body);
        ApplyTo(reply);
        exchange.Out = reply;
    }

    private void RemoveFrom(IMessage message)
    {
        if (RemovedHeaders is null) return;
        foreach (var name in RemovedHeaders)
            message.Headers.Remove(name);
    }
}
