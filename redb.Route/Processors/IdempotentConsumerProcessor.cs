using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

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

        // A redelivery of the whole route (OnException) comes back here with the key this exchange claimed on the
        // previous attempt: it is not a duplicate of itself.
        var claim = ExchangeResources.FindCompletion<KeyClaim>(exchange, c => ReferenceEquals(c.Consumer, this) && c.Key == key);
        var isNew = await _repository.Add(key, ct).ConfigureAwait(false);

        if (!isNew && claim is { State: ClaimState.Done })
        {
            // The block's work is done and a later step failed. Camel redelivers that step alone; skipping the block
            // is the same outcome: the work is not done twice.
            _logger?.LogDebug("Idempotent consumer: key {Key} was processed by this exchange already; the block is skipped on the redelivery.", key);
            return;
        }

        if (!isNew && claim is not { State: ClaimState.Failed })
        {
            // Duplicate detected
            ProcessorMetrics.IdempotentDuplicate.Add(1);
            exchange.Properties[DuplicatePropertyKey] = true;
            _logger?.LogDebug("Duplicate message detected (key={Key}). Skipping.", key);

            if (_skipDuplicate)
            {
                // Tail-consuming wiring: this processor owns the route tail,
                // so duplicates are skipped simply by returning without invoking _inner.
                return;
            }

            // If not skipping, let it through with the duplicate flag
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        // The key is this exchange's: just added, or still held after the block failed on the previous attempt.
        if (claim is null)
        {
            claim = new KeyClaim(this, key);
            ExchangeResources.OnCompletion(exchange, claim);
        }
        if (isNew)
            claim.Bind(Transaction.Current);
        claim.State = ClaimState.Running;

        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
        }
        catch
        {
            claim.State = ClaimState.Failed;
            throw;
        }

        if (exchange.EndedInFailure())
        {
            claim.State = ClaimState.Failed;
            return;
        }
        claim.State = ClaimState.Done;

        // A key bound to a transaction is confirmed with the work; otherwise when the exchange completes.
        if (claim.FollowsTransaction)
            await _repository.Confirm(key, ct).ConfigureAwait(false);
        ProcessorMetrics.IdempotentPassed.Add(1);
    }

    private enum ClaimState { Running, Done, Failed }

    /// <summary>
    /// The key this exchange claimed, and what happens to it when the exchange's unit of work ends (Apache Camel's
    /// idempotent consumer with <c>completionEager=false</c> and <c>removeOnFailure=true</c>, the defaults).
    /// <list type="bullet">
    ///   <item>Claimed outside a transaction: the key is confirmed when the exchange completes and removed when it fails,
    ///   wherever the failure happened (inside the block or after it), so the redelivery is processed again.</item>
    ///   <item>Claimed inside a transaction: the key follows the transaction, not the exchange. It commits with the work
    ///   and stays even if the exchange fails later (a send after the database commit, say), so the redelivery does not
    ///   redo committed work. It goes with a rollback: by itself for a repository that
    ///   <see cref="IIdempotentRepository.JoinsAmbientTransaction"/>, removed by the consumer for one that does not.</item>
    /// </list>
    /// </summary>
    private sealed class KeyClaim : IExchangeCompletion
    {
        public KeyClaim(IdempotentConsumerProcessor consumer, string key)
        {
            Consumer = consumer;
            Key = key;
        }

        public IdempotentConsumerProcessor Consumer { get; }
        public string Key { get; }
        public ClaimState State { get; set; }
        public bool FollowsTransaction { get; private set; }

        public void Bind(Transaction? transaction)
        {
            FollowsTransaction = transaction is not null;
            if (transaction is not null && !Consumer._repository.JoinsAmbientTransaction)
                transaction.TransactionCompleted += RemoveUnlessCommitted;
        }

        public Task OnComplete(IExchange exchange, CancellationToken ct) =>
            FollowsTransaction ? Task.CompletedTask : Consumer._repository.Confirm(Key, ct);

        public async Task OnFailure(IExchange exchange, CancellationToken ct)
        {
            if (FollowsTransaction)
                return;
            try
            {
                await Consumer._repository.Remove(Key, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Idempotent consumer: the exchange failed and removing its key '{Key}' failed too; until the key is " +
                    "removed, the redelivery is skipped as a duplicate.", ex);
            }
        }

        /// <summary>
        /// The repository keeps its keys outside the transaction, so the rollback cannot take the key back: the work it
        /// guarded did not commit, and a key left behind would make the redelivery look like a duplicate. Runs while the
        /// transaction completes, before the route returns to the consumer.
        /// </summary>
        private void RemoveUnlessCommitted(object? sender, TransactionEventArgs e)
        {
            var status = e.Transaction?.TransactionInformation.Status;
            if (status == TransactionStatus.Committed)
                return;
            if (status != TransactionStatus.Aborted)
            {
                Consumer._logger?.LogError(
                    "Idempotent consumer: the outcome of the transaction holding key '{Key}' is {Status}; the key is kept, " +
                    "check whether the work committed.", Key, status);
                return;
            }

            // A synchronous event: the removal is waited for here, so it lands before the consumer hands the message back.
            // It runs outside the completed transaction, which a repository opening a connection must not try to join.
            try
            {
                using var noTransaction = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
                Consumer._repository.Remove(Key, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Consumer._logger?.LogError(ex,
                    "Idempotent consumer: the transaction rolled back and removing key '{Key}' failed; until the key is " +
                    "removed, the redelivery is skipped as a duplicate.", Key);
            }
        }
    }
}
