using System.Collections.ObjectModel;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Default implementation of IMessage.
/// Holds a payload Body and a dictionary of Headers.
/// </summary>
public class Message : IMessage
{
    private readonly Dictionary<string, object?> _headers = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public object? Body { get; set; }

    /// <inheritdoc />
    public string? ContentType { get; set; }

    /// <inheritdoc />
    public IDictionary<string, object?> Headers => _headers;

    /// <summary>Creates an empty message.</summary>
    public Message() { }

    /// <summary>Creates a message with the specified body.</summary>
    /// <param name="body">Initial payload.</param>
    public Message(object? body) => Body = body;

    /// <inheritdoc />
    public T? GetHeader<T>(string key)
    {
        if (!_headers.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T typed)
            return typed;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    /// <inheritdoc />
    public IMessage Clone()
    {
        var clone = new Message(Body) { ContentType = ContentType };
        foreach (var kvp in _headers)
            clone._headers[kvp.Key] = kvp.Value;
        return clone;
    }
}
