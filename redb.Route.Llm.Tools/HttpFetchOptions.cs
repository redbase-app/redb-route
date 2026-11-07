namespace redb.Route.Llm.Tools;

/// <summary>
/// Configuration for <see cref="HttpFetchTool"/>. Defaults are conservative so
/// the tool is safe to expose to a model out of the box; tighten for production.
/// </summary>
public sealed class HttpFetchOptions
{
    /// <summary>
    /// Allowed hosts (case-insensitive, exact match). When non-empty, requests to any
    /// other host are rejected. Empty means "no restriction" \u2014 use only in trusted contexts.
    /// </summary>
    public IReadOnlyCollection<string> HostAllowlist { get; init; } = [];

    /// <summary>
    /// Maximum response body size in bytes. Bytes past the limit are not buffered and
    /// the tool returns a truncation marker. Default 1 MiB.
    /// </summary>
    public int MaxBytes { get; init; } = 1_048_576;

    /// <summary>Request timeout. Default 15 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Endpoint URI the tool is mounted on. Override only if the default collides
    /// with an existing route. Default <c>"direct:llm.http_fetch"</c>.
    /// </summary>
    public string EndpointUri { get; init; } = "direct:llm.http_fetch";

    /// <summary>
    /// Tool name exposed to the model. Override only if the default collides with
    /// another registered tool. Default <c>"http_fetch"</c>.
    /// </summary>
    public string ToolName { get; init; } = "http_fetch";
}
