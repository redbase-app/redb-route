using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;

namespace redb.Route.Llm.Engine;

/// <summary>
/// Resolver for prompt references used by <c>?initialBodyRef=</c> and
/// <c>?systemPromptRef=</c> URI parameters.
/// <para>
/// Convention (matches the framework-wide <c>#name</c> registry-ref pattern
/// already used by Kafka/S3/Firebase connection-factory refs in redb.Route):
/// </para>
/// <list type="bullet">
///   <item><c>null</c> / empty → <c>null</c></item>
///   <item><c>#name</c> → lookup <c>name</c> in <see cref="IPromptTemplateRegistry"/> (latest version),
///         then fall back to <see cref="IRouteContext.GetFromRegistry{T}"/> as a plain string.</item>
///   <item>anything else → returned verbatim as a literal prompt</item>
/// </list>
/// A <c>#name</c> ref that does not resolve in either registry returns <c>null</c> —
/// callers decide whether to substitute an empty string or surface an error.
/// </summary>
internal static class PromptRef
{
    public static async ValueTask<string?> ResolveAsync(
        string? raw,
        IPromptTemplateRegistry? templates,
        IRouteContext? context,
        IExchange? exchange,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw[0] != '#') return raw;

        var name = raw[1..];
        if (name.Length == 0) return null;

        if (templates is not null)
        {
            var tmpl = await templates.GetAsync(name, version: null, exchange, ct).ConfigureAwait(false);
            if (tmpl is not null) return tmpl.Body;
        }

        return context?.GetFromRegistry<string>(name);
    }
}
