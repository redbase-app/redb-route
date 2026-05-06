using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Idempotent consumer processor. Deduplicates exchanges based on a key extracted
/// from the exchange. Duplicate exchanges are silently skipped.
/// <para>
/// Usage: wrap the inner pipeline so that duplicate messages (by key) are not processed twice.
/// The key extractor is a function from <see cref="IExchange"/> to string.
/// Typical keys: message ID, correlation ID, or a business-specific unique identifier.
/// </para>
/// </summary>
public sealed class IdempotentConsumerProcessor : IProcessor
{
    /// <summary>
    /// Well-known exchange property set on duplicates that were skipped.
    /// </summary>
    public const string DuplicatePropertyKey = "CamelDuplicateMessage";

    private readonly IProcessor _inner;
    private readonly IIdempotentRepository _repository;
    private readonly Func<IExchange, string> _keyExtractor;
    private readonly bool _skipDuplicate;
    private readonly ILogger? _logger;

    /// <summary>Creates an idempotent consumer processor.</summary>
    /// <param name="inner">Inner processor to delegate to when the message is not a duplicate.</param>
    /// <param name="repository">Repository for tracking processed message keys.</param>
    /// <param name="keyExtractor">Function that extracts the unique key from an exchange.</param>
    /// <param name="skipDuplicate">
    /// When true, duplicates are silently skipped (default).
    /// When false, duplicates propagate through the pipeline with <c>CamelDuplicateMessage=true</c> property set.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public IdempotentConsumerProcessor(
        IProcessor inner,
        IIdempotentRepository repository,
        Func<IExchange, string> keyExtractor,
        bool skipDuplicate = true,
        ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _keyExtractor = keyExtractor ?? throw new ArgumentNullException(nameof(keyExtractor));
        _skipDuplicate = skipDuplicate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var key = _keyExtractor(exchange);

        if (string.IsNullOrEmpty(key))
        {
            _logger?.LogWarning("Idempotent consumer: key extractor returned null/empty. Processing without dedup.");
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        var isNew = await _repository.Add(key, ct).ConfigureAwait(false);

        if (!isNew)
        {
            // Duplicate detected
            exchange.Properties[DuplicatePropertyKey] = true;
            _logger?.LogDebug("Duplicate message detected (key={Key}). Skipping.", key);

            if (_skipDuplicate)
            {
                exchange.Stop();
                return;
            }

            // If not skipping, let it through with the duplicate flag
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            await _repository.Confirm(key, ct).ConfigureAwait(false);
        }
        catch
        {
            // On failure, remove the key so retries can succeed
            await _repository.Remove(key, ct).ConfigureAwait(false);
            throw;
        }
    }
}
