using redb.Route.Abstractions;

namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// A tool exposable to the LLM. Implementations are declarative — they carry the
/// <see cref="Capability"/> shown to the model and a <see cref="BuildEndpointUri"/>
/// hook that the engine uses to dispatch the call onto a redb.Route endpoint via
/// <c>IProducerTemplate.RequestBody</c>.
/// <para>
/// Descriptors are typically registered as DI singletons (they are stateless metadata).
/// All per-call exchange-scoped state — tenant, conversation, claims, transactions,
/// disposal — flows through the <c>parentExchange</c> argument and is honoured by
/// the underlying redb.Route exchange machinery, not by the descriptor itself.
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
    /// <param name="parentExchange">Parent exchange of the agent route — provides headers, principal, DI scope.</param>
    /// <returns>The endpoint URI the engine will dispatch to.</returns>
    string BuildEndpointUri(string inputJson, IExchange parentExchange);
}
