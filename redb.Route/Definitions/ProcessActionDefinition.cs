using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that executes a synchronous delegate on the exchange.
/// </summary>
public sealed class ProcessActionDefinition : ProcessorDefinition
{
    private readonly Action<IExchange> _action;

    /// <summary>Creates a definition for a synchronous processing action.</summary>
    /// <param name="action">Synchronous action to execute on each exchange.</param>
    public ProcessActionDefinition(Action<IExchange> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = action;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(_action);
}

/// <summary>
/// Leaf definition that executes an asynchronous delegate on the exchange.
/// </summary>
public sealed class ProcessAsyncDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, CancellationToken, Task> _action;

    /// <summary>Creates a definition for an asynchronous processing action.</summary>
    /// <param name="action">Async action to execute on each exchange.</param>
    public ProcessAsyncDefinition(Func<IExchange, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = action;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(_action);
}

/// <summary>
/// Leaf definition that returns a pre-built <see cref="IProcessor"/> instance directly.
/// </summary>
public sealed class ProcessInstanceDefinition : ProcessorDefinition
{
    private readonly IProcessor _processor;

    /// <summary>Creates a definition wrapping an existing processor instance.</summary>
    /// <param name="processor">Pre-built processor to use.</param>
    public ProcessInstanceDefinition(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processor = processor;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => _processor;
}
