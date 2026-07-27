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

    /// <inheritdoc />
    public IMessage Snapshot()
    {
        var snapshot = new Message(DeepCopyBody(Body)) { ContentType = ContentType };
        // Header values are shared like Clone — they carry immutable metadata, not payload.
        foreach (var kvp in _headers)
            snapshot._headers[kvp.Key] = kvp.Value;
        return snapshot;
    }

    /// <summary>
    /// Deep-copies a message body for a checkpoint snapshot. Handles the faithful, non-lossy cases
    /// only: <c>null</c> / <see cref="string"/> / value types are immutable and shared as-is;
    /// <c>byte[]</c> is copied; anything implementing <see cref="ICloneable"/> is cloned via its own
    /// contract (the author is responsible for that clone being deep). Everything else throws — we
    /// never silently share a mutable reference behind a "snapshot", nor round-trip through a
    /// serializer (which would be lossy for many types). A serializer-based deep clone can be added
    /// as an opt-in strategy later without changing this contract.
    /// </summary>
    internal static object? DeepCopyBody(object? body) => body switch
    {
        null => null,
        string => body,                    // immutable
        ValueType => body,                 // int/bool/DateTime/Guid/decimal/enum/tuples/structs — boxed copy is safe to share
        byte[] bytes => bytes.Clone(),     // must precede ICloneable (byte[] is ICloneable but we copy the array)
        ICloneable cloneable => cloneable.Clone(),
        _ => throw new NotSupportedException(
            $"Checkpoint snapshot cannot deep-copy a message body of type '{body.GetType().FullName}'. " +
            "A replayable body must be immutable, a byte[], or implement ICloneable (with deep semantics). " +
            "Materialize it into a snapshot-able form before the .Replayable() marker.")
    };
}
