namespace redb.Route.Llm.Engine;

/// <summary>
/// Decides which headers of the agent's exchange are visible to a tool route.
/// <para>
/// The policy is <b>default-deny</b>: a tool never sees the raw inbound header set
/// (an HTTP consumer's <c>Authorization</c> / <c>Cookie</c> must not leak into every
/// tool call just because the tool is mounted on the same host). Only
/// <see cref="DefaultHeaders"/>, the run's resolved principal / audit tags, and the
/// names the route author opted into via
/// <c>AgentRequest.PropagateToolHeaders</c> are copied.
/// </para>
/// <para>
/// <b>Principal and audit tags are propagated as resolved values, not as raw headers.</b>
/// <c>LlmProducer</c> resolves the principal from the <c>?user=</c> option first
/// (including its <c>${header.X}</c> expression form) and only falls back to the
/// <c>llm.user.id</c> header; copying the header alone would silently drop the
/// option-driven case. Same for <c>?audit=</c> tags. The raw headers are used as a
/// fallback so an engine driven without <c>LlmProducer</c> still propagates them.
/// </para>
/// <para>
/// <b>Trust model:</b> <c>llm.user.id</c> and <c>llm.audit.*</c> are read off the
/// inbound exchange by the producer, so a transport that forwards client headers
/// verbatim (a plain HTTP consumer) lets the caller set them. That is already true
/// for what the engine persists in <c>MessageProps.UserId</c>; propagating the same
/// value to tools does not widen it. The route — not this policy — is responsible
/// for stripping or overwriting client-supplied <c>llm.*</c> headers before the
/// <c>llm://</c> hop when tools make access decisions on them.
/// </para>
/// </summary>
public static class ToolHeaderPolicy
{
    /// <summary>
    /// Headers copied from the agent exchange to every tool call without opt-in:
    /// the conversation id and the two correlation-id spellings the framework
    /// already treats as ambient.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultHeaders =
    [
        LlmHeaders.ConversationId,
        "X-Correlation-Id",
        "CorrelationId"
    ];

    /// <summary>
    /// Fills <paramref name="target"/> with the headers a tool route is allowed to see
    /// for this run. Existing entries in <paramref name="target"/> (the engine's own
    /// <c>llm.tool.*</c> stamps) are never overwritten by propagation.
    /// </summary>
    /// <param name="request">The agent run — source of the parent exchange, resolved principal, audit tags and opt-in list.</param>
    /// <param name="target">Header dictionary of the tool message being built.</param>
    public static void Apply(AgentRequest request, IDictionary<string, object?> target)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);

        var source = request.Exchange.In?.Headers;

        foreach (var name in DefaultHeaders)
            Copy(source, target, name);

        // Principal: resolved value wins over the raw header (see class remarks).
        if (!string.IsNullOrEmpty(request.UserId))
            target[LlmHeaders.UserId] = request.UserId;
        else
            Copy(source, target, LlmHeaders.UserId);

        // Audit tags: same rule — resolved snapshot first, raw llm.audit.* as fallback.
        if (request.AuditTags is { Count: > 0 })
        {
            foreach (var tag in request.AuditTags)
                target[LlmHeaders.AuditTagPrefix + tag.Key] = tag.Value;
        }
        else
        {
            CopyByPrefix(source, target, LlmHeaders.AuditTagPrefix);
        }

        if (request.PropagateToolHeaders is not { Count: > 0 } optIn) return;

        foreach (var pattern in optIn)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            if (pattern.Length > 1 && pattern[^1] == '*')
                CopyByPrefix(source, target, pattern[..^1]);
            else
                Copy(source, target, pattern);
        }
    }

    private static void Copy(IDictionary<string, object?>? from, IDictionary<string, object?> to, string key)
    {
        if (from is null) return;
        if (from.TryGetValue(key, out var v) && v is not null)
            to[key] = v;
    }

    private static void CopyByPrefix(IDictionary<string, object?>? from, IDictionary<string, object?> to, string prefix)
    {
        if (from is null || prefix.Length == 0) return;

        foreach (var kv in from)
        {
            if (kv.Value is null) continue;
            if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            to[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// Parses the CSV form used by the endpoint URI
    /// (<c>?propagateToolHeaders=x-tenant-id,x-app-*</c>) into a name list.
    /// Returns null for null/empty input so callers can leave the field unset.
    /// </summary>
    /// <param name="csv">Comma-separated header names; a trailing <c>*</c> marks a prefix match.</param>
    public static IReadOnlyList<string>? ParseCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;

        var names = new List<string>();
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = System.Web.HttpUtility.UrlDecode(raw).Trim();
            if (name.Length > 0) names.Add(name);
        }
        return names.Count == 0 ? null : names;
    }
}
