using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using StackExchange.Redis;

namespace redb.Route.Redis;

/// <summary>
/// Redis endpoint. Parses the operation type and resource from the URI path.
/// Manages a shared <see cref="IConnectionMultiplexer"/> and <see cref="IDatabase"/>.
///
/// <para>Path format: <c>OPERATION:resource</c> or <c>OPERATION/resource</c>.</para>
/// <para>Example: <c>redis:SET:user:123?ttl=300</c> → OperationType=SET, Resource="user:123".</para>
/// </summary>
public sealed class RedisEndpoint : EndpointBase<RedisEndpointOptions>, IDisposable
{
    private IConnectionMultiplexer? _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>Parsed Redis operation type.</summary>
    public RedisOperationType OperationType { get; }

    /// <summary>Resource portion of the path (key, channel, stream name, etc.).</summary>
    public string Resource { get; }

    /// <summary>Logger from the parent component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Typed options (exposed for consumer/producer).</summary>
    internal RedisEndpointOptions EndpointOptions => Options;

    /// <summary>Creates a Redis endpoint.</summary>
    public RedisEndpoint(EndpointUri uri, RedisComponent component, RedisEndpointOptions options)
        : base(uri, component, options)
    {
        Logger = component.Logger;
        (OperationType, Resource) = ParsePath(uri.Path);

        // Apply resource to the correct option field based on operation type
        if (IsPubSubOperation(OperationType) && string.IsNullOrEmpty(options.Channel))
            options.Channel = Resource;
        else if (IsStreamOperation(OperationType) && string.IsNullOrEmpty(options.StreamName))
            options.StreamName = Resource;
        else if (string.IsNullOrEmpty(options.Key))
            options.Key = Resource;
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new RedisProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        if (OperationType == RedisOperationType.XGROUP && string.IsNullOrEmpty(Options.ConsumerGroup))
            throw new InvalidOperationException("ConsumerGroup is required for XGROUP consumers.");

        if (IsPubSubOperation(OperationType) && string.IsNullOrEmpty(Options.Channel))
            throw new InvalidOperationException("Channel is required for Pub/Sub consumers.");

        return new RedisConsumer(this, processor, Options);
    }

    /// <summary>Gets or creates a shared <see cref="IConnectionMultiplexer"/>.</summary>
    internal async Task<IConnectionMultiplexer> GetOrCreateConnectionAsync(CancellationToken ct = default)
    {
        if (_connection is { IsConnected: true })
            return _connection;

        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsConnected: true })
                return _connection;

            var config = BuildConfiguration();
            _connection = await ConnectionMultiplexer.ConnectAsync(config).ConfigureAwait(false);

            _connection.ConnectionFailed += (_, args) =>
                Logger?.LogError("Redis connection failed: {EndPoint} - {FailureType}", args.EndPoint, args.FailureType);
            _connection.ConnectionRestored += (_, args) =>
                Logger?.LogInformation("Redis connection restored: {EndPoint}", args.EndPoint);

            Logger?.LogInformation("Redis connection established: {Endpoints}",
                string.Join(", ", _connection.GetEndPoints().Select(e => e.ToString())));

            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Gets an <see cref="IDatabase"/> from the connection.</summary>
    internal async Task<IDatabase> GetDatabaseAsync(CancellationToken ct = default)
    {
        var conn = await GetOrCreateConnectionAsync(ct).ConfigureAwait(false);
        return conn.GetDatabase(Options.Database);
    }

    /// <summary>Gets a <see cref="ISubscriber"/> for Pub/Sub.</summary>
    internal async Task<ISubscriber> GetSubscriberAsync(CancellationToken ct = default)
    {
        var conn = await GetOrCreateConnectionAsync(ct).ConfigureAwait(false);
        return conn.GetSubscriber();
    }

    /// <inheritdoc />
    public override async Task Stop(CancellationToken ct = default)
    {
        if (_connection is not null)
        {
            try { await _connection.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger?.LogWarning(ex, "Error closing Redis connection"); }
            _connection.Dispose();
            _connection = null;
        }

        Logger?.LogInformation("Redis endpoint stopped: {Uri}", Uri);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        _connectionLock.Dispose();
    }

    // ── Helpers ──

    private ConfigurationOptions BuildConfiguration()
    {
        // 1. Try named factory from registry
        if (!string.IsNullOrEmpty(Options.ConnectionFactory))
        {
            var component = Component as RedisComponent;
            var registryFactory = component?.Context?.GetFromRegistry<RedisConnectionFactory>(Options.ConnectionFactory);
            if (registryFactory is not null)
            {
                Logger?.LogDebug("Redis: using ConnectionFactory '{Name}' from registry", Options.ConnectionFactory);
                return registryFactory.Build();
            }

            Logger?.LogWarning("Redis: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                Options.ConnectionFactory);
        }

        // 2. Fallback: build from URI parameters
        var config = ConfigurationOptions.Parse(Options.ConnectionString);
        config.DefaultDatabase = Options.Database;
        config.AbortOnConnectFail = false;

        if (!string.IsNullOrEmpty(Options.Password))
            config.Password = Options.Password;

        return config;
    }

    /// <summary>
    /// Parses "OPERATION:resource" or "OPERATION/resource" from the URI path.
    /// </summary>
    private static (RedisOperationType Op, string Resource) ParsePath(string path)
    {
        // Path may use : or / as separator between operation and resource
        // Examples: "SET:user:123", "SET/user/123", "SUBSCRIBE:notifications*"
        int separatorIndex = -1;

        // Find the first separator that splits operation from resource
        // The operation is always a single uppercase word (SET, GET, SUBSCRIBE, etc.)
        var colonIndex = path.IndexOf(':');
        var slashIndex = path.IndexOf('/');

        if (colonIndex > 0 && (slashIndex < 0 || colonIndex < slashIndex))
            separatorIndex = colonIndex;
        else if (slashIndex > 0)
            separatorIndex = slashIndex;

        string operationName;
        string resource;

        if (separatorIndex > 0)
        {
            operationName = path[..separatorIndex];
            resource = path[(separatorIndex + 1)..];
        }
        else
        {
            operationName = path;
            resource = string.Empty;
        }

        if (!Enum.TryParse<RedisOperationType>(operationName, ignoreCase: true, out var op))
            throw new ArgumentException($"Unknown Redis operation: '{operationName}'. Use redis:OPERATION:resource format.");

        return (op, resource);
    }

    internal static bool IsPubSubOperation(RedisOperationType op) =>
        op is RedisOperationType.PUBLISH or RedisOperationType.SUBSCRIBE or RedisOperationType.PSUBSCRIBE;

    internal static bool IsStreamOperation(RedisOperationType op) =>
        op is RedisOperationType.XADD or RedisOperationType.XREAD or RedisOperationType.XGROUP;

    internal static bool IsListBlockingOperation(RedisOperationType op) =>
        op is RedisOperationType.BLPOP or RedisOperationType.BRPOP;
}
