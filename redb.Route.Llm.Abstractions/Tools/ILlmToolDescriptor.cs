using redb.Route.Abstractions;

namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// A tool exposable to the LLM. Implementations are declarative — they carry the
/// <see cref="Capability"/> shown to the model and a <see cref="BuildEndpointUri"/>
/// hook that the engine uses to dispatch the call onto a redb.Route endpoint via
/// <c>IProducerTemplate.RequestBody</c>.
/// <para>
/// Descriptors are typically registered as DI singletons (they are stateless metadata).
/// Per-call state is not carried by the descriptor: the engine dispatches the tool on a
/// <b>linked child</b> of the agent exchange, so the parent's <c>Properties</c>,
/// <c>RouteId</c>, DI scope (hence every scoped service the conversation resolved) and
/// ambient transaction are the tool's as well.
/// </para>
/// <para>
/// <b>Headers are the exception — they are filtered, not inherited.</b> A tool receives
/// the conversation id, both correlation-id spellings, the run's resolved principal
/// (<c>llm.user.id</c>) and its resolved audit tags (<c>llm.audit.*</c>), plus whatever
/// the route author named via <c>.PropagateToolHeaders(...)</c>. The inbound transport's
/// header set is never forwarded implicitly. See <c>ToolHeaderPolicy</c> in
/// <c>redb.Route.Llm</c>.
/// </para>
/// </summary>
public interface ILlmToolDescriptor
{
    /// <summary>Capability metadata used by the engine to govern the call and project the tool to the provider.</summary>
    LlmToolCapability Capability { get; }

    /// <summary>
    /// Computes the endpoint URI to which the engine forwards the tool call.
    /// May return a static URI (e.g. <c>direct:sql-readonly</c>) or build one
    /// dynamically from the input JSON (e.g. <c>http://{input.url}</c>).
    /// </summary>
    /// <param name="inputJson">Tool input — JSON object matching <see cref="LlmToolCapability.InputSchema"/>.</param>
    /// <param name="parentExchange">
    /// Parent exchange of the agent route — read-only view of the run for address
    /// building (headers, properties, DI scope). Note that only what this method
    /// encodes into the returned URI, plus the propagated header set, reaches the
    /// tool route; do not use it to smuggle per-call state.
    /// </param>
    /// <returns>The endpoint URI the engine will dispatch to.</returns>
    string BuildEndpointUri(string inputJson, IExchange parentExchange);
}
