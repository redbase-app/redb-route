using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Processes a message exchange asynchronously using a delegate function.
/// Simplest processor — wraps a user-provided Func.
/// </summary>
public class DelegateProcessor : IProcessor
{
    private readonly Func<IExchange, CancellationToken, Task> _action;

    /// <summary>Creates a processor from an async delegate.</summary>
    /// <param name="action">The async action to execute on each exchange.</param>
    public DelegateProcessor(Func<IExchange, CancellationToken, Task> action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    /// <summary>Creates a processor from a synchronous delegate.</summary>
    /// <param name="action">The synchronous action to execute on each exchange.</param>
    public DelegateProcessor(Action<IExchange> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = (ex, _) => { action(ex); return Task.CompletedTask; };
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
        => _action(exchange, ct);
}
