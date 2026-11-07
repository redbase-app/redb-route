using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using redb.Route.Abstractions;

namespace redb.Route.Cache;

/// <summary>
/// How a body that is neither text nor bytes becomes bytes: through the context's data format for the
/// message's content type (JSON when the message names none, or names one without a serializer). One
/// rule for the distributed store and for <c>KeyFromBody()</c>, so what is hashed is what is stored.
/// </summary>
internal static class CacheSerialization
{
    private static readonly MethodInfo SerializeDefinition =
        typeof(IMessageSerializer).GetMethod(nameof(IMessageSerializer.Serialize))!;

    // IMessageSerializer.Serialize<T> is generic, so the call is closed over the body type once, not on
    // every write. The table holds its keys weakly: a type from an unloaded plugin is not kept alive here.
    private static readonly ConditionalWeakTable<Type, MethodInfo> SerializeMethods = new();

    /// <summary>Serializes <paramref name="body"/>; the serializer's own exception comes out, not the reflection wrapper.</summary>
    public static (byte[] Bytes, IMessageSerializer Serializer) Serialize(IDataFormatRegistry? registry, object body, string? contentType)
    {
        var type = body.GetType();
        var wanted = contentType ?? "application/json";
        var serializer = registry?.GetSerializer(wanted) ?? registry?.GetSerializer("application/json")
            ?? throw new InvalidOperationException($"Cache: no data format for '{wanted}' to serialize a {type.Name} body.");
        var method = SerializeMethods.GetValue(type, static t => SerializeDefinition.MakeGenericMethod(t));
        try
        {
            return ((byte[])method.Invoke(serializer, [body])!, serializer);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
