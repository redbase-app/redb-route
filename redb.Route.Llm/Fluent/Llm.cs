using System.Text;
using System.Web;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm.Fluent;

/// <summary>
/// Fluent API for LLM endpoints.
/// <example><code>
/// .To(Llm.Factory("claude").Temperature(0.2).MaxTokens(1024))
/// .To(Llm.Factory("claude").Stream())
/// </code></example>
/// </summary>
public static class Llm
{
    /// <summary>Creates an LLM endpoint targeting the named connection factory.</summary>
    public static LlmBuilder Factory(string connectionFactoryName) => new(connectionFactoryName);
}

/// <summary>Fluent builder for LLM endpoint URIs.</summary>
public sealed class LlmBuilder
{
    private readonly string _factory;

    private string? _temperature;
    private string? _maxTokens;
    private string? _topP;
    private string? _systemPromptRef;
    private string? _conversation;
    private bool _stream;
    private string? _schedule;
    private string? _initialBodyRef;
    private string? _maxIterations;
    private string? _tools;

    internal LlmBuilder(string factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        _factory = factory;
    }

    /// <summary>Sampling temperature.</summary>
    public LlmBuilder Temperature(double v) { _temperature = v.ToString(System.Globalization.CultureInfo.InvariantCulture); return this; }

    /// <summary>Maximum output tokens.</summary>
    public LlmBuilder MaxTokens(int v) { _maxTokens = v.ToString(); return this; }

    /// <summary>Top-p sampling.</summary>
    public LlmBuilder TopP(double v) { _topP = v.ToString(System.Globalization.CultureInfo.InvariantCulture); return this; }

    /// <summary>Reference (or literal) for system-prompt template.</summary>
    public LlmBuilder SystemPromptRef(string r) { _systemPromptRef = r; return this; }

    /// <summary>Tracks conversation by reading <c>llm.conversation.id</c> header.</summary>
    public LlmBuilder ConversationFromHeader() { _conversation = "header"; return this; }

    /// <summary>Tracks conversation using the route id as conversation key.</summary>
    public LlmBuilder ConversationFromRoute() { _conversation = "property"; return this; }

    /// <summary>Enables streaming mode.</summary>
    public LlmBuilder Stream() { _stream = true; return this; }

    /// <summary>Schedule for consumer mode (cron or fixed interval).</summary>
    public LlmBuilder Schedule(string s) { _schedule = s; return this; }

    /// <summary>Initial body template for scheduled consumers.</summary>
    public LlmBuilder InitialBody(string bodyRef) { _initialBodyRef = bodyRef; return this; }

    /// <summary>Maximum tool-loop iterations.</summary>
    public LlmBuilder MaxIterations(int n) { _maxIterations = n.ToString(); return this; }

    /// <summary>
    /// Tool exposure filter — <c>"*"</c> for every descriptor in the registry,
    /// or a CSV of tool names (e.g. <c>"get_order,get_invoice"</c>).
    /// </summary>
    public LlmBuilder Tools(string filter) { _tools = filter; return this; }

    /// <summary>Exposes every descriptor registered in the global tool registry.</summary>
    public LlmBuilder UseAllTools() { _tools = "*"; return this; }

    /// <summary>Implicit conversion to <see cref="EndpointUri"/> for use in <c>.To(...)</c> / <c>.From(...)</c>.</summary>
    public static implicit operator EndpointUri(LlmBuilder b) => EndpointUriParser.Parse(b.ToString());

    /// <summary>Returns the URI string. Useful for <c>.To(builder.AsUri())</c> overloads that take string.</summary>
    public string AsUri() => ToString();

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder("llm://").Append(HttpUtility.UrlEncode(_factory));
        var first = true;
        Append(sb, ref first, "temperature", _temperature);
        Append(sb, ref first, "maxTokens", _maxTokens);
        Append(sb, ref first, "topP", _topP);
        Append(sb, ref first, "systemPromptRef", _systemPromptRef);
        Append(sb, ref first, "conversation", _conversation);
        if (_stream) Append(sb, ref first, "stream", "true");
        Append(sb, ref first, "schedule", _schedule);
        Append(sb, ref first, "initialBodyRef", _initialBodyRef);
        Append(sb, ref first, "maxIterations", _maxIterations);
        Append(sb, ref first, "tools", _tools);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, ref bool first, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append(first ? '?' : '&');
        first = false;
        sb.Append(key).Append('=').Append(HttpUtility.UrlEncode(value));
    }
}
