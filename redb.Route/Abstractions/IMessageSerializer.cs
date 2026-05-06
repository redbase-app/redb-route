namespace redb.Route.Abstractions;

/// <summary>
/// Defines a strategy for serializing and deserializing message bodies.
/// Implementations handle specific formats (JSON, Protobuf, MessagePack, etc.).
/// </summary>
public interface IMessageSerializer
{
    /// <summary>Content type this serializer handles (e.g., "application/json").</summary>
    string ContentType { get; }

    /// <summary>
    /// All media types this serializer can handle, including structured-suffix aliases
    /// (e.g. <c>application/json</c>, <c>application/vnd.api+json</c>, <c>application/problem+json</c>).
    /// Default implementation returns a single-element list containing <see cref="ContentType"/>.
    /// Implementations override this to claim multiple media types in a single registration.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="IDataFormatRegistry"/> during <c>Register</c> so that one
    /// serializer instance covers all its aliases without repeated calls.
    /// </remarks>
    IReadOnlyCollection<string> MediaTypes => new[] { ContentType };

    /// <summary>Serializes a value to a byte array.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="value">Value to serialize.</param>
    /// <returns>Serialized bytes.</returns>
    byte[] Serialize<T>(T value);

    /// <summary>Deserializes a byte array to a typed value.</summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="data">Serialized bytes.</param>
    /// <returns>Deserialized value.</returns>
    T? Deserialize<T>(byte[] data);

    /// <summary>Deserializes a byte array to a typed value.</summary>
    /// <param name="data">Serialized bytes.</param>
    /// <param name="type">Target type.</param>
    /// <returns>Deserialized value.</returns>
    object? Deserialize(byte[] data, Type type);
}
