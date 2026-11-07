using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Outermost pipeline wrapper that registers/unregisters exchanges
/// in the <see cref="IInflightRepository"/>. Wraps even StatisticsProcessor.
/// Guarantees unregister via finally block.
/// </summary>
internal sealed class InflightTrackingProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly IInflightRepository _repository;
    private readonly string _routeId;
    private readonly string? _fromEndpoint;

    public InflightTrackingProcessor(
        IProcessor inner,
        IInflightRepository repository,
        string routeId,
        string? fromEndpoint = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _routeId = routeId;
        _fromEndpoint = fromEndpoint;
    }

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var entry = new InflightExchange(
            exchange.ExchangeId,
            _routeId,
            DateTime.UtcNow,
            Environment.CurrentManagedThreadId,
            _fromEndpoint);

        _repository.Register(entry);
        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            _repository.Unregister(exchange.ExchangeId);
        }
    }
}
