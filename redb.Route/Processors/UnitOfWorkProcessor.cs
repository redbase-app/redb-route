using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Opens the exchange's unit of work when it enters its first route and ends it before that route returns to its consumer
/// (Apache Camel: the <c>UnitOfWork</c> of the consumer's route, done before the consumer acknowledges). The completions
/// registered with <see cref="ExchangeResources.OnCompletion"/> have therefore run before the message is acknowledged or
/// handed back to the broker, so a redelivery cannot overtake them.
/// <para>
/// Sits outside every error handler and the route's <c>OnCompletion</c> blocks, and reaches the consumer's verdict: an
/// escaping exception is a failure, and so is a normal return with an unhandled failure left on the exchange
/// (<c>OnException</c> without <c>Handled</c>) or a rollback-only mark (<c>.RollbackAll()</c>) —
/// <see cref="ExchangeFailureExtensions.EndedInFailure"/>. A handled failure is a success. A route called with an exchange
/// whose unit of work is already open (<c>direct:</c>) passes through.
/// </para>
/// </summary>
internal sealed class UnitOfWorkProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly ILogger? _logger;

    public UnitOfWorkProcessor(IProcessor inner, ILogger? logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger;
    }

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var unitOfWork = ExchangeResources.OpenUnitOfWork(exchange);
        if (unitOfWork is null)
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
        }
        catch
        {
            await unitOfWork.End(exchange, failed: true, _logger).ConfigureAwait(false);
            throw;
        }
        await unitOfWork.End(exchange, exchange.EndedInFailure(), _logger).ConfigureAwait(false);
    }
}
