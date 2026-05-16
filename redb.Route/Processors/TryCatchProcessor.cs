using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// A catch clause used by <see cref="TryCatchProcessor"/>.
/// Matches exceptions by type and executes a handler processor.
/// </summary>
public class CatchClause
{
    /// <summary>The exception type to catch.</summary>
    public Type ExceptionType { get; }

    /// <summary>Optional predicate for additional filtering.</summary>
    public Func<Exception, bool>? When { get; }

    /// <summary>Processor to execute when the exception is caught.</summary>
    public IProcessor Handler { get; }

    /// <summary>Creates a catch clause.</summary>
    /// <param name="exceptionType">Exception type to match.</param>
    /// <param name="handler">Handler processor.</param>
    /// <param name="when">Optional additional predicate.</param>
    public CatchClause(Type exceptionType, IProcessor handler, Func<Exception, bool>? when = null)
    {
        ExceptionType = exceptionType ?? throw new ArgumentNullException(nameof(exceptionType));
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));

        if (!typeof(Exception).IsAssignableFrom(exceptionType))
            throw new ArgumentException($"Type must be an Exception, got {exceptionType.Name}", nameof(exceptionType));

        When = when;
    }

    /// <summary>Creates a typed catch clause.</summary>
    /// <typeparam name="TException">Exception type to match.</typeparam>
    /// <param name="handler">Handler processor.</param>
    /// <param name="when">Optional additional predicate.</param>
    /// <returns>A new catch clause for the specified type.</returns>
    public static CatchClause For<TException>(IProcessor handler, Func<TException, bool>? when = null)
        where TException : Exception
    {
        Func<Exception, bool>? wrappedWhen = when != null
            ? ex => ex is TException typed && when(typed)
            : null;

        return new CatchClause(typeof(TException), handler, wrappedWhen);
    }

    /// <summary>Checks whether this clause matches the given exception.</summary>
    /// <param name="exception">The exception to match.</param>
    /// <returns>True if this clause handles the exception.</returns>
    public bool Matches(Exception exception)
    {
        if (!ExceptionType.IsInstanceOfType(exception))
            return false;

        return When == null || When(exception);
    }
}

/// <summary>
/// Try-catch error handling processor. Wraps a body processor and catches
/// exceptions using ordered catch clauses. Optionally runs a finally block.
/// </summary>
public class TryCatchProcessor : IProcessor
{
    private readonly IProcessor _body;
    private readonly List<CatchClause> _catchClauses = [];
    private readonly ILogger? _logger;
    private IProcessor? _finally;

    /// <summary>Gets the catch clauses.</summary>
    public IReadOnlyList<CatchClause> CatchClauses => _catchClauses;

    /// <summary>Gets the finally processor.</summary>
    public IProcessor? Finally => _finally;

    /// <summary>Creates a try-catch processor wrapping the specified body.</summary>
    /// <param name="body">The processor to wrap in try-catch.</param>
    /// <param name="logger">Optional logger.</param>
    public TryCatchProcessor(IProcessor body, ILogger? logger = null)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _logger = logger;
    }

    /// <summary>Adds a catch clause.</summary>
    /// <param name="clause">The catch clause to add.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public TryCatchProcessor Catch(CatchClause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        _catchClauses.Add(clause);
        return this;
    }

    /// <summary>Adds a typed catch clause.</summary>
    /// <typeparam name="TException">Exception type to catch.</typeparam>
    /// <param name="handler">Handler processor.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public TryCatchProcessor Catch<TException>(IProcessor handler) where TException : Exception
    {
        _catchClauses.Add(CatchClause.For<TException>(handler));
        return this;
    }

    /// <summary>Sets the finally processor.</summary>
    /// <param name="processor">Processor to run after try/catch regardless of outcome.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public TryCatchProcessor SetFinally(IProcessor processor)
    {
        _finally = processor ?? throw new ArgumentNullException(nameof(processor));
        return this;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        try
        {
            await _body.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            exchange.Exception = ex;

            var matched = false;
            foreach (var clause in _catchClauses)
            {
                if (clause.Matches(ex))
                {
                    _logger?.LogWarning(ex, "TryCatch: caught {ExceptionType}: {Message}",
                        ex.GetType().Name, ex.Message);
                    await clause.Handler.Process(exchange, ct).ConfigureAwait(false);
                    exchange.ExceptionHandled = true;
                    matched = true;
                    break;
                }
            }

            if (!matched)
                throw;
        }
        finally
        {
            if (_finally != null)
            {
                await _finally.Process(exchange, ct).ConfigureAwait(false);
            }
        }
    }
}
