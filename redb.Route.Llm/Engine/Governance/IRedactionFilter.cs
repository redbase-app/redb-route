namespace redb.Route.Llm.Engine.Governance;

/// <summary>
/// Removes secrets / PII from strings before they are logged, persisted to
/// <see cref="Storage.IConversationStore"/>, or sent to observers. Applied to
/// both inbound user content and outbound model content, plus tool input/output.
/// </summary>
public interface IRedactionFilter
{
    /// <summary>
    /// Returns the redacted form of <paramref name="text"/>. Implementations
    /// must be deterministic so re-saves don't re-redact already-masked tokens.
    /// </summary>
    string Redact(string text, RedactionContext context);
}

/// <summary>Context flags that influence how a piece of text is redacted.</summary>
[Flags]
public enum RedactionContext
{
    /// <summary>Unspecified.</summary>
    None = 0,
    /// <summary>Text is part of the user-supplied prompt.</summary>
    UserInput = 1,
    /// <summary>Text is model-generated content.</summary>
    ModelOutput = 2,
    /// <summary>Text is tool input JSON.</summary>
    ToolInput = 4,
    /// <summary>Text is tool output JSON.</summary>
    ToolOutput = 8,
    /// <summary>Text is going to be logged (vs stored).</summary>
    Log = 16
}

/// <summary>Default redaction filter — returns the input untouched.</summary>
public sealed class NoopRedactionFilter : IRedactionFilter
{
    /// <inheritdoc />
    public string Redact(string text, RedactionContext context) => text;
}
