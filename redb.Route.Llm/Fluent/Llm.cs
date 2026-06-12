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
    private string? _user;
    private readonly List<KeyValuePair<string, string>> _auditTags = [];
    private string? _promptTemplateName;
    private string? _promptTemplateVersion;

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

    /// <summary>
    /// Principal identifier for audit — literal value (<c>"system"</c>) or a
    /// header reference (<c>"${header.X-User-Id}"</c>). The producer resolves
    /// this expression pre-call against the inbound exchange and stamps the
    /// resolved value on every persisted row of the run as
    /// <c>MessageProps.UserId</c>. When unset, <c>LlmHeaders.UserId</c>
    /// (<c>llm.user.id</c>) is used as a fallback.
    /// </summary>
    public LlmBuilder User(string expression) { _user = expression; return this; }

    /// <summary>
    /// Adds one audit tag to the run. <paramref name="value"/> may be a literal
    /// or a <c>${header.X}</c> expression. The producer resolves the value
    /// pre-call and stamps the (key, value) pair on every persisted row as
    /// <c>MessageProps.AuditTags</c> entry. Headers named
    /// <c>llm.audit.&lt;key&gt;</c> merge on top of these; headers win on
    /// collision. Repeatable — call once per tag.
    /// </summary>
    public LlmBuilder Audit(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _auditTags.Add(new KeyValuePair<string, string>(key, value ?? string.Empty));
        return this;
    }

    /// <summary>
    /// Locks the prompt-template (name, version) pair onto every persisted row
    /// of the run as <c>MessageProps.PromptTemplateName</c> /
    /// <c>PromptTemplateVersion</c>. Independent of <see cref="SystemPromptRef"/>
    /// — you can carry name/version even when the prompt body comes from
    /// elsewhere.
    /// </summary>
    public LlmBuilder PromptTemplate(string name, string version)
    {
        _promptTemplateName = name;
        _promptTemplateVersion = version;
        return this;
    }

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
        Append(sb, ref first, "user", _user);
        Append(sb, ref first, "audit", BuildAuditCsv(_auditTags));
        Append(sb, ref first, "promptTemplateName", _promptTemplateName);
        Append(sb, ref first, "promptTemplateVersion", _promptTemplateVersion);
        return sb.ToString();
    }

    private static string? BuildAuditCsv(List<KeyValuePair<string, string>> tags)
    {
        if (tags.Count == 0) return null;
        var sb = new StringBuilder();
        for (var i = 0; i < tags.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(HttpUtility.UrlEncode(tags[i].Key))
              .Append('=')
              .Append(HttpUtility.UrlEncode(tags[i].Value));
        }
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
