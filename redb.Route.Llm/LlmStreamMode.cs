namespace redb.Route.Llm;

/// <summary>How an <c>llm:</c> step streams (<c>stream=</c>).</summary>
public enum LlmStreamMode
{
    /// <summary>No streaming (the option is absent): every model call waits for the whole answer.</summary>
    Off,

    /// <summary>
    /// <c>stream=calls</c>: the agent engine makes every model call as a stream, inside the route. Tools, the
    /// conversation, the budget and the route's transaction work as without streaming; the pieces go to
    /// <see cref="Engine.Observability.IAgentObserver.OnDeltaAsync"/>, and <c>Out.Body</c> is the final text. The
    /// connection carries data while the model writes, which keeps long calls alive through tunnels and proxies.
    /// </summary>
    Calls,

    /// <summary>
    /// <c>stream=body</c>: <c>Out.Body</c> is an <see cref="IAsyncEnumerable{T}"/> of strings, and the agent run —
    /// tools, conversation, budget — happens when the body is read, after the route, the way a streamed SQL result is
    /// read. The visible text of every model call streams as the model writes it, for HTTP (SSE), WebSocket and gRPC
    /// consumers to forward; the summary headers arrive after the run. The body belongs to its exchange: it is read
    /// once, released with the exchange, and refused by <c>RequestBody</c>. Refused inside <c>.Transacted()</c>: the
    /// block would commit before the answer exists; use <see cref="Calls"/> there.
    /// </summary>
    Body
}
