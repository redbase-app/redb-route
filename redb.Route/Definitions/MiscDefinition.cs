using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that begins an imperative transaction scope on the exchange.
/// </summary>
public sealed class BeginTransactionDefinition : ProcessorDefinition
{
    private readonly TransactionPolicy? _policy;

    /// <summary>Creates a begin-transaction definition.</summary>
    /// <param name="policy">Transaction policy (null = default Required).</param>
    public BeginTransactionDefinition(TransactionPolicy? policy = null) { _policy = policy; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<BeginTransactionProcessor>();
        return new BeginTransactionProcessor(_policy, logger);
    }
}

/// <summary>
/// Leaf definition that commits the imperative transaction scope and all deferred actions.
/// </summary>
public sealed class CommitTransactionDefinition : ProcessorDefinition
{
    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<CommitTransactionProcessor>();
        return new CommitTransactionProcessor(logger);
    }
}

/// <summary>
/// Leaf definition that rolls back the imperative transaction scope and all deferred actions.
/// </summary>
public sealed class RollbackTransactionDefinition : ProcessorDefinition
{
    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<RollbackTransactionProcessor>();
        return new RollbackTransactionProcessor(logger);
    }
}

/// <summary>
/// Leaf definition that rolls back all transacted actions registered on the exchange properties.
/// </summary>
public sealed class RollbackAllDefinition : ProcessorDefinition
{
    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(async (exchange, ct) =>
        {
            if (exchange.Properties.TryGetValue(TransactedProcessor.TransactActionPropertyKey, out var raw) &&
                raw is System.Collections.Concurrent.ConcurrentDictionary<string, ITransactedAction> actions)
            {
                foreach (var kvp in actions)
                    await kvp.Value.Rollback(ct).ConfigureAwait(false);
                actions.Clear();
            }
            exchange.Properties["RollbackOnly"] = true;
        });
}

/// <summary>
/// Leaf definition that sets the exchange pattern.
/// </summary>
public sealed class SetPatternDefinition : ProcessorDefinition
{
    private readonly ExchangePattern _pattern;

    /// <summary>Creates a set-pattern definition.</summary>
    public SetPatternDefinition(ExchangePattern pattern) { _pattern = pattern; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.Pattern = _pattern);
}

/// <summary>
/// Leaf definition that creates an Out response by applying a factory to the exchange.
/// </summary>
public sealed class RespondDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, object?> _factory;

    /// <summary>Creates a respond definition.</summary>
    public RespondDefinition(Func<IExchange, object?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange =>
        {
            var responseBody = _factory(exchange);
            exchange.Out = exchange.In.Clone();
            exchange.Out.Body = responseBody;
        });
}

/// <summary>
/// Leaf definition that resolves a DI service and invokes a method on it (service activator/bean).
/// </summary>
public sealed class BeanDefinition : ProcessorDefinition
{
    private readonly Type _serviceType;
    private readonly Func<object, IExchange, CancellationToken, Task> _method;

    /// <summary>Service type resolved from DI.</summary>
    public Type ServiceType => _serviceType;

    /// <summary>Normalized delegate invoked with (service, exchange, ct).</summary>
    public Func<object, IExchange, CancellationToken, Task> Method => _method;

    /// <summary>Creates a bean definition.</summary>
    /// <param name="serviceType">Service type to resolve from the exchange's ServiceProvider.</param>
    /// <param name="method">Normalized delegate: (service, exchange, ct) → Task.</param>
    public BeanDefinition(Type serviceType, Func<object, IExchange, CancellationToken, Task> method)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(method);
        _serviceType = serviceType;
        _method = method;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var serviceType = _serviceType;
        var method = _method;
        return new DelegateProcessor(async (exchange, ct) =>
        {
            var provider = exchange.ServiceProvider
                ?? throw new InvalidOperationException(
                    $"Bean<{serviceType.Name}> requires a ServiceProvider on the exchange. " +
                    "Ensure the route context is configured with DI.");
            var service = provider.GetRequiredService(serviceType);
            await method(service, exchange, ct).ConfigureAwait(false);
        });
    }
}

/// <summary>
/// Leaf definition that wraps a forward-only <see cref="Stream"/> body with a seekable
/// <see cref="Configuration.StreamCacheOptions"/>-backed cache, enabling multiple re-reads.
/// Non-Stream bodies pass through unchanged.
/// Equivalent to Apache Camel's <c>streamCaching()</c> inline DSL step.
/// </summary>
public sealed class StreamCachingDefinition : ProcessorDefinition
{
    private readonly long? _spoolThreshold;

    /// <summary>Creates a stream caching definition.</summary>
    /// <param name="spoolThreshold">Bytes threshold before spooling to disk (null = default 128 KB).</param>
    public StreamCachingDefinition(long? spoolThreshold = null) { _spoolThreshold = spoolThreshold; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var globalOptions = context.GetService<Configuration.StreamCacheOptions>()
            ?? new Configuration.StreamCacheOptions();
        var options = _spoolThreshold.HasValue
            ? new Configuration.StreamCacheOptions
            {
                SpoolThreshold = _spoolThreshold.Value,
                TempDirectory = globalOptions.TempDirectory,
            }
            : globalOptions;
        return new Processors.StreamCachingTransformer(options);
    }
}

