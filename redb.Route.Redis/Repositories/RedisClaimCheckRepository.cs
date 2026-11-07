using System;
using System.Threading;
using System.Threading.Tasks;
using redb.Route.Abstractions;
using StackExchange.Redis;

namespace redb.Route.Redis.Repositories;

/// <summary>
/// Redis-backed implementation of <see cref="IClaimCheckRepository"/>.
/// Uses Redis STRING commands for binary-safe storage with native TTL.
/// Thread-safe; manages its own connection via <see cref="RedisConnectionFactory"/>.
/// </summary>
public sealed class RedisClaimCheckRepository : IClaimCheckRepository, IAsyncDisposable, IDisposable
{
    private readonly RedisConnectionFactory _factory;
    private readonly string _keyPrefix;
    private readonly TimeSpan _defaultTtl;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private IConnectionMultiplexer? _connection;

    // Lua script for atomic GET+DEL (GETDEL available only in Redis 6.2+)
    private const string GetAndDeleteScript = """
        local val = redis.call('GET', KEYS[1])
        if val then redis.call('DEL', KEYS[1]) end
        return val
        """;

    /// <summary>
    /// Creates a new Redis claim check repository.
    /// </summary>
    /// <param name="factory">Redis connection factory with connection settings.</param>
    /// <param name="keyPrefix">Key prefix for all claim check entries (default: "redb:claimcheck:").</param>
    /// <param name="defaultTtl">Default TTL for entries. Zero means no expiry.</param>
    public RedisClaimCheckRepository(
        RedisConnectionFactory factory,
        string keyPrefix = "redb:claimcheck:",
        TimeSpan? defaultTtl = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _keyPrefix = keyPrefix;
        _defaultTtl = defaultTtl ?? TimeSpan.Zero;
    }

    /// <inheritdoc />
    public async Task<string> Store(ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var key = Guid.NewGuid().ToString("N");
        await StoreInternal(key, data, ttl, ct).ConfigureAwait(false);
        return key;
    }

    /// <inheritdoc />
    public async Task Store(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await StoreInternal(key, data, ttl, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> Retrieve(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var db = await GetDatabaseAsync(ct).ConfigureAwait(false);
        var value = await db.StringGetAsync(PrefixKey(key)).ConfigureAwait(false);
        return value.IsNull ? null : (byte[])value!;
    }

    /// <inheritdoc />
    public async Task<byte[]?> RetrieveAndRemove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var db = await GetDatabaseAsync(ct).ConfigureAwait(false);
        var result = await db.ScriptEvaluateAsync(
            GetAndDeleteScript,
            [(RedisKey)PrefixKey(key)]).ConfigureAwait(false);

        return result.IsNull ? null : (byte[])result!;
    }

    /// <inheritdoc />
    public async Task Remove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var db = await GetDatabaseAsync(ct).ConfigureAwait(false);
        await db.KeyDeleteAsync(PrefixKey(key)).ConfigureAwait(false);
    }

    private async Task StoreInternal(string key, ReadOnlyMemory<byte> data, TimeSpan? ttl, CancellationToken ct)
    {
        var db = await GetDatabaseAsync(ct).ConfigureAwait(false);
        var effectiveTtl = ttl ?? _defaultTtl;
        var redisKey = (RedisKey)PrefixKey(key);
        var redisValue = (RedisValue)data.ToArray();

        if (effectiveTtl > TimeSpan.Zero)
            await db.StringSetAsync(redisKey, redisValue, effectiveTtl).ConfigureAwait(false);
        else
            await db.StringSetAsync(redisKey, redisValue).ConfigureAwait(false);
    }

    private string PrefixKey(string key) => $"{_keyPrefix}{key}";

    private async Task<IDatabase> GetDatabaseAsync(CancellationToken ct)
    {
        if (_connection is { IsConnected: true })
            return _connection.GetDatabase(_factory.Database);

        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsConnected: true })
                return _connection.GetDatabase(_factory.Database);

            var config = _factory.Build();
            _connection = await ConnectionMultiplexer.ConnectAsync(config).ConfigureAwait(false);
            return _connection.GetDatabase(_factory.Database);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_connection is not null)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
                _connection.Dispose();
                _connection = null;
            }
        }
        finally
        {
            _connectionLock.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
        _connectionLock.Dispose();
    }
}
