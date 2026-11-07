using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Content-hash keys for the tool cache. Two layers, one question ("do I already have an output for
/// this exact input?"): the in-process run-scoped memo answers inside a run, the store answers across
/// runs. <c>Memoize</c> never reaches the store — an entry keyed for one run could only be read by
/// that run, so it would be unreadable garbage that nothing ever evicts.
/// <para>
/// Both keys carry the RESOLVED endpoint address, the tool's POLICY fingerprint and the CALLER
/// fingerprint, not the tool name alone: <see cref="ILlmToolDescriptor.BuildEndpointUri"/> may address a
/// per-tenant / per-principal endpoint, and a tool route also receives the caller's identity and the
/// headers the route opted into propagating (see <c>ToolHeaderPolicy</c>) — so "same input" is not the
/// same answer for two callers. An entry written while the tool was laxer must not answer a call made
/// after it got stricter either. The address is folded in as a hash and never stored or logged raw —
/// URIs may carry sensitive parameters, and so may a caller identity.
/// </para>
/// <para>
/// The tool name, by contrast, is kept in the clear as a label: operators reading the cache scheme need
/// to tell whose rows they are looking at, and a server-side query can scope a scheme to one tool by it
/// (REDB keeps props out of the object columns, so the stored key is the only place a tool name can
/// live). Everything security-relevant still goes through the hash.
/// </para>
/// </summary>
internal static class ToolCacheKey
{
    /// <summary>Key for the in-process run-scoped memo layer.</summary>
    public static string MemoKey(
        string toolName, string resolvedEndpointUri, string policyFingerprint, string callerFingerprint, string inputJson)
        => $"memo:{toolName}:{Hash(toolName, resolvedEndpointUri, policyFingerprint, callerFingerprint, inputJson)}";

    /// <summary>Key for the persistent store; <c>null</c> for <c>None</c> and <c>Memoize</c>.</summary>
    public static string? StoreKey(
        string toolName, string resolvedEndpointUri, string inputJson, ToolCachingPolicy policy,
        string policyFingerprint, string callerFingerprint)
        => policy == ToolCachingPolicy.Persist
            ? $"persist:{toolName}:{Hash(toolName, resolvedEndpointUri, policyFingerprint, callerFingerprint, inputJson)}"
            : null;

    /// <summary>
    /// Fingerprint of the tool's governance policy, folded into both keys. An entry written while a
    /// tool was laxer must never answer a call made after the tool got stricter — adding a required
    /// claim or changing the caching policy invalidates the old entries instead of quietly serving
    /// their output. Claims are length-prefixed (as in <c>ToolSetHash</c>), so splitting one claim in
    /// two cannot collide with a single claim that merely contains a separator.
    /// </summary>
    public static string PolicyFingerprint(LlmToolSafety safety)
    {
        var sb = new StringBuilder();
        sb.Append(safety.SideEffect).Append('/').Append(safety.Caching).Append('/')
            .Append(safety.RequiresApproval ? '1' : '0');
        foreach (var claim in safety.RequiredClaims.OrderBy(c => c, StringComparer.Ordinal))
            sb.Append('\n').Append(claim.Length).Append(':').Append(claim);
        return sb.ToString();
    }

    /// <summary>Relative TTL of a persisted entry (owner-tunable; not yet a route option).</summary>
    public static TimeSpan StoreTtl => TimeSpan.FromHours(24);

    private static string Hash(
        string toolName, string resolvedEndpointUri, string policyFingerprint, string callerFingerprint, string inputJson)
    {
        var canonical = Canonicalize(inputJson);
        var payload = $"{toolName}\n{resolvedEndpointUri}\n{policyFingerprint}\n{callerFingerprint}\n{canonical}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    /// <summary>
    /// Canonical form of the input JSON: object properties sorted by name, recursively. Two texts that
    /// differ only in key order mean the same tool call, and a cache that disagreed would report a miss
    /// for an identical request. Anything that is not valid JSON is used verbatim — a key that is
    /// merely conservative is correct, a key built on a parse failure is not.
    /// </summary>
    private static string Canonicalize(string inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
                WriteCanonical(writer, doc.RootElement);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return inputJson;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
