using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Processor for the Claim Check EIP pattern.
/// Handles all five operations: Set, Get, GetAndRemove, Push, Pop.
/// Store operations replace the exchange body with a claim key.
/// Retrieve operations restore the exchange body from the repository.
/// </summary>
public sealed class ClaimCheckProcessor : IProcessor
{
    private readonly IClaimCheckRepository _repository;
    private readonly ClaimCheckOperation _operation;
    private readonly string? _key;
    private readonly TimeSpan? _ttl;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new Claim Check processor.
    /// </summary>
    /// <param name="repository">The claim check repository.</param>
    /// <param name="operation">The operation to perform.</param>
    /// <param name="key">Explicit key for Set/Get/GetAndRemove. Ignored for Push/Pop.</param>
    /// <param name="ttl">TTL for stored data. Only used by Set/Push.</param>
    /// <param name="logger">Optional logger.</param>
    public ClaimCheckProcessor(
        IClaimCheckRepository repository,
        ClaimCheckOperation operation,
        string? key = null,
        TimeSpan? ttl = null,
        ILogger? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _operation = operation;
        _key = key;
        _ttl = ttl;
        _logger = logger;

        // Validate key requirements
        if (_operation is ClaimCheckOperation.Set or ClaimCheckOperation.Get or ClaimCheckOperation.GetAndRemove
            && string.IsNullOrEmpty(_key))
        {
            // Key can also come from header at runtime, so we don't throw here
        }
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        switch (_operation)
        {
            case ClaimCheckOperation.Set:
                await StoreBody(exchange, _key, ct).ConfigureAwait(false);
                break;

            case ClaimCheckOperation.Push:
                await PushBody(exchange, ct).ConfigureAwait(false);
                break;

            case ClaimCheckOperation.Get:
                await RetrieveBody(exchange, remove: false, ct).ConfigureAwait(false);
                break;

            case ClaimCheckOperation.GetAndRemove:
                await RetrieveBody(exchange, remove: true, ct).ConfigureAwait(false);
                break;

            case ClaimCheckOperation.Pop:
                await PopBody(exchange, ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown claim check operation: {_operation}");
        }
    }

    private async Task StoreBody(IExchange exchange, string? explicitKey, CancellationToken ct)
    {
        var body = exchange.In.Body;
        var data = ClaimCheckSerializer.Serialize(body);

        // Save original type info in headers
        SaveBodyMetadata(exchange, body);

        string claimKey;
        if (!string.IsNullOrEmpty(explicitKey))
        {
            await _repository.Store(explicitKey, data, _ttl, ct).ConfigureAwait(false);
            claimKey = explicitKey;
        }
        else
        {
            claimKey = await _repository.Store(data, _ttl, ct).ConfigureAwait(false);
        }

        exchange.In.Headers[ClaimCheckHeaders.Key] = claimKey;
        exchange.In.Body = claimKey;

        _logger?.LogDebug("Claim check: stored body under key {Key} ({Bytes} bytes)",
            claimKey, data.Length);
    }

    private async Task PushBody(IExchange exchange, CancellationToken ct)
    {
        var body = exchange.In.Body;
        var data = ClaimCheckSerializer.Serialize(body);

        SaveBodyMetadata(exchange, body);

        var claimKey = await _repository.Store(data, _ttl, ct).ConfigureAwait(false);

        // Push key onto the per-exchange stack
        var stack = GetOrCreateStack(exchange);
        stack.Push(claimKey);

        exchange.In.Headers[ClaimCheckHeaders.Key] = claimKey;
        exchange.In.Body = claimKey;

        _logger?.LogDebug("Claim check: pushed body as key {Key} (stack depth: {Depth})",
            claimKey, stack.Count);
    }

    private async Task RetrieveBody(IExchange exchange, bool remove, CancellationToken ct)
    {
        var claimKey = ResolveKey(exchange);
        if (string.IsNullOrEmpty(claimKey))
        {
            _logger?.LogWarning("Claim check: no claim key found for {Operation}. Skipping.", _operation);
            return;
        }

        var data = remove
            ? await _repository.RetrieveAndRemove(claimKey, ct).ConfigureAwait(false)
            : await _repository.Retrieve(claimKey, ct).ConfigureAwait(false);

        if (data is null)
        {
            _logger?.LogWarning("Claim check: no data found for key {Key} (operation={Operation})",
                claimKey, _operation);
            return;
        }

        RestoreBody(exchange, data);

        _logger?.LogDebug("Claim check: restored body from key {Key} ({Bytes} bytes, remove={Remove})",
            claimKey, data.Length, remove);
    }

    private async Task PopBody(IExchange exchange, CancellationToken ct)
    {
        var stack = GetExistingStack(exchange);
        if (stack is null || !stack.TryPop(out var claimKey))
        {
            _logger?.LogWarning("Claim check: pop called but stack is empty.");
            return;
        }

        var data = await _repository.RetrieveAndRemove(claimKey, ct).ConfigureAwait(false);
        if (data is null)
        {
            _logger?.LogWarning("Claim check: pop found key {Key} on stack but data is missing.", claimKey);
            return;
        }

        RestoreBody(exchange, data);

        _logger?.LogDebug("Claim check: popped body from key {Key} (stack depth: {Depth})",
            claimKey, stack.Count);
    }

    private string? ResolveKey(IExchange exchange)
    {
        // Explicit key from step configuration takes priority
        if (!string.IsNullOrEmpty(_key))
            return _key;

        // Fall back to key from header
        if (exchange.In.Headers.TryGetValue(ClaimCheckHeaders.Key, out var headerKey)
            && headerKey is string keyStr && !string.IsNullOrEmpty(keyStr))
            return keyStr;

        // Fall back to body as key (e.g., when body was replaced with key by Set)
        if (exchange.In.Body is string bodyKey && !string.IsNullOrEmpty(bodyKey))
            return bodyKey;

        return null;
    }

    private static void SaveBodyMetadata(IExchange exchange, object? body)
    {
        if (exchange.In.ContentType != null)
            exchange.In.Headers[ClaimCheckHeaders.OriginalContentType] = exchange.In.ContentType;

        if (body != null)
            exchange.In.Headers[ClaimCheckHeaders.OriginalBodyType] = body.GetType().FullName;
    }

    private static void RestoreBody(IExchange exchange, byte[] data)
    {
        var originalType = exchange.In.Headers.TryGetValue(ClaimCheckHeaders.OriginalBodyType, out var typeObj)
            ? typeObj as string
            : null;

        exchange.In.Body = ClaimCheckSerializer.Deserialize(data, originalType);

        // Restore original content type
        if (exchange.In.Headers.TryGetValue(ClaimCheckHeaders.OriginalContentType, out var ct))
        {
            exchange.In.ContentType = ct as string;
            exchange.In.Headers.Remove(ClaimCheckHeaders.OriginalContentType);
        }

        // Clean up metadata headers
        exchange.In.Headers.Remove(ClaimCheckHeaders.OriginalBodyType);
        exchange.In.Headers.Remove(ClaimCheckHeaders.Key);
    }

    private static Stack<string> GetOrCreateStack(IExchange exchange)
    {
        if (exchange.Properties.TryGetValue(ClaimCheckHeaders.StackPropertyKey, out var existing)
            && existing is Stack<string> stack)
            return stack;

        var newStack = new Stack<string>();
        exchange.Properties[ClaimCheckHeaders.StackPropertyKey] = newStack;
        return newStack;
    }

    private static Stack<string>? GetExistingStack(IExchange exchange)
    {
        if (exchange.Properties.TryGetValue(ClaimCheckHeaders.StackPropertyKey, out var existing)
            && existing is Stack<string> stack)
            return stack;

        return null;
    }
}
