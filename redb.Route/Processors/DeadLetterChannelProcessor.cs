using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Wraps an inner processor and, on uncaught exception, sends the exchange to the configured
/// dead-letter URI before re-throwing (when configured as transaction-aware so the outer
/// transaction can still roll back) or swallowing the exception (best-effort handle mode).
/// Apache Camel parity: <c>errorHandler(deadLetterChannel("..."))</c>.
/// Producer is cached for the lifetime of the processor.
/// </summary>
public sealed class DeadLetterChannelProcessor : IProcessor
{
    private readonly IRouteContext _context;
    private readonly IProcessor _inner;
    private readonly string _deadLetterUri;
    private readonly bool _rethrow;
    private readonly ILogger? _logger;
    private IProducer? _producer;
    private readonly SemaphoreSlim _producerLock = new(1, 1);

    /// <summary>Creates a dead-letter channel processor.</summary>
    /// <param name="context">Route context used to resolve the dead-letter endpoint.</param>
    /// <param name="inner">Inner processor whose failures should be dead-lettered.</param>
    /// <param name="deadLetterUri">URI of the dead-letter endpoint.</param>
    /// <param name="rethrow">If true, the original exception is re-thrown after dead-lettering
    /// (so an enclosing transactional scope can still roll back). Default true to match
    /// Camel's transactional semantics.</param>
    /// <param name="logger">Optional logger.</param>
    public DeadLetterChannelProcessor(
        IRouteContext context,
        IProcessor inner,
        string deadLetterUri,
        bool rethrow = true,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(deadLetterUri);
        _context = context;
        _inner = inner;
        _deadLetterUri = deadLetterUri;
        _rethrow = rethrow;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            exchange.Exception = ex;
            _logger?.LogWarning(ex, "Dead-lettering exchange to {Uri}.", _deadLetterUri);
            try
            {
                var producer = await GetOrCreateProducer(ct).ConfigureAwait(false);
                await producer.Process(exchange, ct).ConfigureAwait(false);
                ProcessorMetrics.DeadLetterSent.Add(1);
            }
            catch (Exception sendEx)
            {
                _logger?.LogError(sendEx, "Dead-letter delivery to {Uri} failed.", _deadLetterUri);
            }

            if (_rethrow) throw;
            exchange.Exception = null;
        }
    }

    private async Task<IProducer> GetOrCreateProducer(CancellationToken ct)
    {
        if (_producer is not null) return _producer;
        await _producerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_producer is not null) return _producer;
            var endpoint = _context.GetEndpoint(_deadLetterUri);
            var producer = endpoint.CreateProducer();
            await producer.Start(ct).ConfigureAwait(false);
            (_context as RouteContext)?.TrackProducer(producer);
            _producer = producer;
            return producer;
        }
        finally
        {
            _producerLock.Release();
        }
    }
}
