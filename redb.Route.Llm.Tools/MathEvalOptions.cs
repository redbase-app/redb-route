namespace redb.Route.Llm.Tools;

/// <summary>
/// Configuration for <see cref="MathEvalTool"/>.
/// </summary>
public sealed class MathEvalOptions
{
    /// <summary>Endpoint URI the tool is mounted on. Default <c>"direct:llm.math_eval"</c>.</summary>
    public string EndpointUri { get; init; } = "direct:llm.math_eval";

    /// <summary>Tool name exposed to the model. Default <c>"math_eval"</c>.</summary>
    public string ToolName { get; init; } = "math_eval";

    /// <summary>
    /// Maximum length of the inbound expression in characters. Default 4096.
    /// Guards against pathological input.
    /// </summary>
    public int MaxExpressionChars { get; init; } = 4096;
}
