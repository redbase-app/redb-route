using System;
using System.Text;
using System.Text.Json;

namespace redb.Route.Processors;

/// <summary>
/// Default serialization helper for the Claim Check pattern.
/// Handles byte[], string, and arbitrary objects (via JSON).
/// </summary>
internal static class ClaimCheckSerializer
{
    /// <summary>
    /// Serializes a message body to a byte array.
    /// </summary>
    /// <param name="body">The body to serialize. May be null, byte[], string, or any serializable object.</param>
    /// <returns>Binary representation of the body.</returns>
    public static byte[] Serialize(object? body)
    {
        if (body is null) return [];
        if (body is byte[] bytes) return bytes;
        if (body is string str) return Encoding.UTF8.GetBytes(str);
        if (body is ReadOnlyMemory<byte> rom) return rom.ToArray();
        if (body is Memory<byte> mem) return mem.ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(body, body.GetType());
    }

    /// <summary>
    /// Deserializes a byte array back to a message body.
    /// Returns the data as a string (UTF-8) by default; callers can use ConvertBody afterwards.
    /// </summary>
    /// <param name="data">The binary data to deserialize.</param>
    /// <param name="originalBodyType">Original CLR type name, or null.</param>
    /// <returns>The deserialized body.</returns>
    public static object? Deserialize(byte[] data, string? originalBodyType)
    {
        if (data.Length == 0) return null;

        // If original was byte[], return byte[]
        if (originalBodyType == typeof(byte[]).FullName)
            return data;

        // Default: return as UTF-8 string
        return Encoding.UTF8.GetString(data);
    }
}
