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
/// Close the scope with <see cref="EndIdempotentConsumer"/>.
/// </summary>
public class IdempotentConsumerDefinition : RouteDefinition, IRouteScope
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
        IProcessor inner = Outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => Outputs[0].CreateProcessor(context),
            _ => BuildPipeline(context)
        };

        var loggerFactory = context.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<IdempotentConsumerProcessor>();

        var repo = _repository ?? context
            .GetIdempotentRepositoryProvider()
            .Get(_repositoryName!);

        return new IdempotentConsumerProcessor(inner, repo, _keyExtractor, _skipDuplicate, logger);
    }

    private PipelineProcessor BuildPipeline(IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var output in Outputs)
            pipeline.Add(output.CreateProcessor(context));
        return pipeline;
    }

    /// <summary>Closes this idempotent consumer scope and returns the parent route definition.</summary>
    /// <exception cref="InvalidOperationException">Thrown if no parent route is set.</exception>
    public IRouteDefinition EndIdempotentConsumer()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndIdempotentConsumer() called without a parent route. Ensure IdempotentConsumer() was called on a route definition."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndIdempotentConsumer();

    // ── Leaf DSL (mirror of RouteDefinition) ─────────────────────────────────

    /// <summary>Sends the exchange to an endpoint.</summary>
    public IdempotentConsumerDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes the exchange with a synchronous action.</summary>
    public IdempotentConsumerDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes the exchange with an asynchronous action.</summary>
    public IdempotentConsumerDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes the exchange with a pre-built processor instance.</summary>
    public IdempotentConsumerDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body to a static value.</summary>
    public IdempotentConsumerDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Sets the exchange body using a factory.</summary>
    public IdempotentConsumerDefinition SetBody(Func<IExchange, object?> factory) { AddOutput(new SetBodyFactoryDefinition(factory)); return this; }

    /// <summary>Sets the exchange body using an <see cref="IExpression"/>.</summary>
    public IdempotentConsumerDefinition SetBody(IExpression expression) { AddOutput(new SetBodyExpressionDefinition(expression)); return this; }

    /// <summary>Transforms the exchange body.</summary>
    public IdempotentConsumerDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Removes the exchange body.</summary>
    public IdempotentConsumerDefinition RemoveBody() { AddOutput(new RemoveBodyDefinition()); return this; }

    /// <summary>Sets a header to a static value.</summary>
    public IdempotentConsumerDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Removes a header.</summary>
    public IdempotentConsumerDefinition RemoveHeader(string key) { AddOutput(new RemoveHeaderDefinition(key)); return this; }

    /// <summary>Sets a property to a static value.</summary>
    public IdempotentConsumerDefinition SetProperty(string key, object? value) { AddOutput(new SetPropertyStaticDefinition(key, value)); return this; }

    /// <summary>Removes a property.</summary>
    public IdempotentConsumerDefinition RemoveProperty(string key) { AddOutput(new RemovePropertyDefinition(key)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public IdempotentConsumerDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}
