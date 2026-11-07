using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Idempotent Consumer EIP.
/// When <see cref="CreateProcessor"/> is called, compiles all child <see cref="ProcessorDefinition.Outputs"/>
/// into an inner pipeline wrapped by an <see cref="IdempotentConsumerProcessor"/>.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>;
/// close the scope with <see cref="EndIdempotentConsumer"/>.
/// </summary>
public class IdempotentConsumerDefinition : RouteDefinitionBase<IdempotentConsumerDefinition>, IRouteScope
{
    private readonly IIdempotentRepository? _repository;
    private readonly string? _repositoryName;
    private readonly Func<IExchange, string> _keyExtractor;
    private readonly bool _skipDuplicate;

    internal IdempotentConsumerDefinition(
        IIdempotentRepository repository,
        Func<IExchange, string> keyExtractor,
        bool skipDuplicate = true)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(keyExtractor);
        _repository = repository;
        _keyExtractor = keyExtractor;
        _skipDuplicate = skipDuplicate;
    }

    internal IdempotentConsumerDefinition(
        string repositoryName,
        Func<IExchange, string> keyExtractor,
        bool skipDuplicate = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositoryName);
        ArgumentNullException.ThrowIfNull(keyExtractor);
        _repositoryName = repositoryName;
        _keyExtractor = keyExtractor;
        _skipDuplicate = skipDuplicate;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor inner = NodePipeline.Body(context, Outputs);

        var loggerFactory = context.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<IdempotentConsumerProcessor>();

        var repo = _repository ?? context
            .GetIdempotentRepositoryProvider()
            .Get(_repositoryName!);

        return new IdempotentConsumerProcessor(inner, repo, _keyExtractor, _skipDuplicate, logger);
    }

    /// <summary>Closes this idempotent consumer scope and returns the parent route definition.</summary>
    /// <exception cref="InvalidOperationException">Thrown if no parent route is set.</exception>
    public IRouteDefinition EndIdempotentConsumer()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndIdempotentConsumer() called without a parent route. Ensure IdempotentConsumer() was called on a route definition."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndIdempotentConsumer();
}
