using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Engine;

namespace redb.Route.Llm;

/// <summary>
/// LLM endpoint. The URI host is interpreted as the connection-factory name.
/// </summary>
public sealed class LlmEndpoint : EndpointBase<LlmEndpointOptions>
{
    /// <summary>Connection factory name (from URI host or <c>connectionFactory</c> query).</summary>
    public string ConnectionFactoryName { get; }

    /// <summary>Resolved connection factory from the registry (null if not registered yet).</summary>
    internal LlmConnectionFactory? ResolvedFactory { get; }

    /// <summary>
    /// Agent engine resolved from the route context. Created lazily on first producer/consumer
    /// instantiation so that engine wiring can happen after the endpoint is created.
    /// </summary>
    internal IAgentEngine? ResolvedEngine { get; }

    /// <summary>Creates an LLM endpoint.</summary>
    public LlmEndpoint(EndpointUri uri, LlmComponent component, LlmEndpointOptions options)
        : base(uri, component, options)
    {
        // Path wins; fall back to query parameter
        ConnectionFactoryName = !string.IsNullOrWhiteSpace(uri.Path)
            ? uri.Path
            : (options.ConnectionFactory ?? string.Empty);

        if (string.IsNullOrWhiteSpace(ConnectionFactoryName))
            throw new InvalidOperationException(
                "LLM endpoint requires a connection factory name. " +
                "Use 'llm://<factoryName>' or '?connectionFactory=<name>'.");

        if (component.Context is not null)
        {
            ResolvedFactory = component.Context.GetFromRegistry<LlmConnectionFactory>(ConnectionFactoryName);
            ResolvedEngine = component.Context.GetService<IAgentEngine>();
        }
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new LlmProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor) => new LlmConsumer(this, Options, processor);
}
