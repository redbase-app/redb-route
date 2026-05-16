namespace redb.Route.Abstractions;

/// <summary>
/// Represents a message flowing through the route pipeline.
/// Contains payload (Body), metadata (Headers), and supports both
/// C# idiomatic properties and Java-style methods for backward compatibility.
/// </summary>
public interface IMessage
{
    // ── C# idiomatic API ──

    /// <summary>Message payload. Can be any object or null.</summary>
    object? Body { get; set; }

    /// <summary>
    /// MIME content type of the message body (e.g. "application/json").
    /// Set by transport consumers from protocol-native metadata.
    /// Null means unknown — no assumptions about body format.
    /// </summary>
    string? ContentType { get; set; }

    /// <summary>
    /// Headers that travel with the message (ContentType, CorrelationId, custom).
    /// These headers are propagated to brokers when sending.
    /// </summary>
    IDictionary<string, object?> Headers { get; }

    /// <summary>Gets a header value with type conversion.</summary>
    /// <typeparam name="T">Target type for the header value.</typeparam>
    /// <param name="key">Header key.</param>
    /// <returns>Converted value or default if not found.</returns>
    T? GetHeader<T>(string key);

    /// <summary>Deep copy of the message including Body and Headers.</summary>
    IMessage Clone();

    // ── Java-style API (kept forever, default interface methods) ──

    /// <summary>Java-style: returns Body.</summary>
    object? getBody() => Body;

    /// <summary>Java-style: returns Body cast to <typeparamref name="T"/>.</summary>
    T? getBody<T>()
    {
        if (Body is null) return default;
        if (Body is T typed) return typed;
        try { return (T)Convert.ChangeType(Body, typeof(T)); }
        catch { return default; } // conversion failure — return default by design
    }

    /// <summary>Java-style: sets Body.</summary>
    void setBody(object? value) => Body = value;

    /// <summary>Java-style: returns ContentType.</summary>
    string? getContentType() => ContentType;

    /// <summary>Java-style: sets ContentType.</summary>
    void setContentType(string? value) => ContentType = value;

    /// <summary>Java-style: returns header value by key.</summary>
    object? getHeader(string key) => Headers.TryGetValue(key, out var v) ? v : null;

    /// <summary>Java-style: returns header value cast to <typeparamref name="T"/>.</summary>
    T? getHeader<T>(string key) => GetHeader<T>(key);

    /// <summary>Java-style: sets header value.</summary>
    void setHeader(string key, object? value) => Headers[key] = value;

    /// <summary>Java-style: returns Headers dictionary.</summary>
    IDictionary<string, object?> getHeaders() => Headers;

    /// <summary>Java-style: removes a header by key.</summary>
    void removeHeader(string key) => Headers.Remove(key);

    /// <summary>Java-style: deep copy (alias for Clone).</summary>
    IMessage copy() => Clone();
}
