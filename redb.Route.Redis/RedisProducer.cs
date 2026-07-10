using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using StackExchange.Redis;

namespace redb.Route.Redis;

/// <summary>
/// Redis producer. Dispatches to the correct Redis command based on
/// <see cref="RedisEndpoint.OperationType"/>. Supports all major Redis data structures:
/// strings, lists, hashes, sets, sorted sets, pub/sub, streams, geo, HyperLogLog, bitmap.
/// Transacted mode defers writes via <see cref="ITransactedAction"/>.
/// </summary>
public sealed class RedisProducer : ConnectableProducer
{
    private readonly RedisEndpoint _endpoint;
    private readonly RedisEndpointOptions _options;

    private IDatabase? _db;
    private ISubscriber? _subscriber;

    /// <summary>Creates a Redis producer.</summary>
    public RedisProducer(RedisEndpoint endpoint, RedisEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"redis:{_endpoint.OperationType}:{_endpoint.Resource}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _db = await _endpoint.GetDatabaseAsync(ct).ConfigureAwait(false);

        if (RedisEndpoint.IsPubSubOperation(_endpoint.OperationType))
            _subscriber = await _endpoint.GetSubscriberAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"redis {_endpoint.OperationType}", ActivityKind.Client,
            "db.system", "redis",
            _endpoint.Uri.NormalizedKey,
            destination: _endpoint.Resource,
            operation: _endpoint.OperationType.ToString());

        if (_options.Transacted && IsWriteOperation(_endpoint.OperationType))
        {
            ProcessTransactional(exchange);
            return;
        }

        try
        {
            await DispatchOperationAsync(exchange, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogError(ex, "Redis {Operation} failed: resource={Resource}, connected={IsConnected}, db={Database}",
                _endpoint.OperationType, _endpoint.Resource, _db?.Multiplexer?.IsConnected, _db?.Database);
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Operation dispatch
    // ═══════════════════════════════════════════════════════════

    private async Task DispatchOperationAsync(IExchange exchange, CancellationToken ct)
    {
        switch (_endpoint.OperationType)
        {
            // Key/Value
            case RedisOperationType.SET:
            case RedisOperationType.GET:
            case RedisOperationType.DEL:
            case RedisOperationType.EXISTS:
            case RedisOperationType.EXPIRE:
            case RedisOperationType.INCR:
            case RedisOperationType.DECR:
            case RedisOperationType.SETNX:
                await ProcessCacheAsync(exchange).ConfigureAwait(false);
                break;

            // Pub/Sub
            case RedisOperationType.PUBLISH:
                await ProcessPublishAsync(exchange).ConfigureAwait(false);
                break;

            // Streams
            case RedisOperationType.XADD:
                await ProcessStreamAddAsync(exchange).ConfigureAwait(false);
                break;

            // Lists
            case RedisOperationType.LPUSH:
            case RedisOperationType.RPUSH:
            case RedisOperationType.LPOP:
            case RedisOperationType.RPOP:
            case RedisOperationType.LLEN:
            case RedisOperationType.LRANGE:
                await ProcessListAsync(exchange).ConfigureAwait(false);
                break;

            // Hashes
            case RedisOperationType.HSET:
            case RedisOperationType.HGET:
            case RedisOperationType.HMSET:
            case RedisOperationType.HMGET:
            case RedisOperationType.HGETALL:
            case RedisOperationType.HDEL:
            case RedisOperationType.HLEN:
                await ProcessHashAsync(exchange).ConfigureAwait(false);
                break;

            // Sets
            case RedisOperationType.SADD:
            case RedisOperationType.SREM:
            case RedisOperationType.SMEMBERS:
            case RedisOperationType.SCARD:
            case RedisOperationType.SISMEMBER:
                await ProcessSetAsync(exchange).ConfigureAwait(false);
                break;

            // Sorted Sets
            case RedisOperationType.ZADD:
            case RedisOperationType.ZREM:
            case RedisOperationType.ZRANGE:
            case RedisOperationType.ZCARD:
            case RedisOperationType.ZSCORE:
            case RedisOperationType.ZRANGEBYSCORE:
                await ProcessSortedSetAsync(exchange).ConfigureAwait(false);
                break;

            // Geo
            case RedisOperationType.GEOADD:
            case RedisOperationType.GEODIST:
            case RedisOperationType.GEORADIUS:
                await ProcessGeoAsync(exchange).ConfigureAwait(false);
                break;

            // HyperLogLog
            case RedisOperationType.PFADD:
            case RedisOperationType.PFCOUNT:
            case RedisOperationType.PFMERGE:
                await ProcessHyperLogLogAsync(exchange).ConfigureAwait(false);
                break;

            // Bitmap
            case RedisOperationType.SETBIT:
            case RedisOperationType.GETBIT:
            case RedisOperationType.BITCOUNT:
                await ProcessBitmapAsync(exchange).ConfigureAwait(false);
                break;

            // Arbitrary
            case RedisOperationType.COMMAND:
                await ProcessCommandAsync(exchange).ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException($"Producer does not support operation: {_endpoint.OperationType}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Key/Value (Cache)
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessCacheAsync(IExchange exchange)
    {
        var key = ResolveKey(exchange);

        switch (_endpoint.OperationType)
        {
            case RedisOperationType.SET:
                var value = exchange.In.Body?.ToString() ?? string.Empty;
                if (_options.Ttl > 0)
                    await _db!.StringSetAsync(key, value, TimeSpan.FromSeconds(_options.Ttl)).ConfigureAwait(false);
                else
                    await _db!.StringSetAsync(key, value).ConfigureAwait(false);
                SetResult(exchange, "OK");
                break;

            case RedisOperationType.GET:
                var result = await _db!.StringGetAsync(key).ConfigureAwait(false);
                SetResult(exchange, result.HasValue ? result.ToString() : null);
                break;

            case RedisOperationType.DEL:
                var deleted = await _db!.KeyDeleteAsync(key).ConfigureAwait(false);
                SetResult(exchange, deleted);
                break;

            case RedisOperationType.EXISTS:
                var exists = await _db!.KeyExistsAsync(key).ConfigureAwait(false);
                SetResult(exchange, exists);
                break;

            case RedisOperationType.EXPIRE:
                var ttl = _options.Ttl > 0 ? TimeSpan.FromSeconds(_options.Ttl) : TimeSpan.FromHours(1);
                var expired = await _db!.KeyExpireAsync(key, ttl).ConfigureAwait(false);
                SetResult(exchange, expired);
                break;

            case RedisOperationType.INCR:
                var incremented = await _db!.StringIncrementAsync(key).ConfigureAwait(false);
                SetResult(exchange, incremented);
                break;

            case RedisOperationType.DECR:
                var decremented = await _db!.StringDecrementAsync(key).ConfigureAwait(false);
                SetResult(exchange, decremented);
                break;

            case RedisOperationType.SETNX:
                var setnxValue = exchange.In.Body?.ToString() ?? string.Empty;
                bool wasSet;
                if (_options.Ttl > 0)
                    wasSet = await _db!.StringSetAsync(key, setnxValue, TimeSpan.FromSeconds(_options.Ttl), When.NotExists).ConfigureAwait(false);
                else
                    wasSet = await _db!.StringSetAsync(key, setnxValue, when: When.NotExists).ConfigureAwait(false);
                SetResult(exchange, wasSet ? "OK" : null);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Pub/Sub
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessPublishAsync(IExchange exchange)
    {
        var channel = _options.ResolveOption(_options.Channel ?? _endpoint.Resource, exchange)
                      ?? _endpoint.Resource;
        var message = exchange.In.Body?.ToString() ?? string.Empty;
        var recipients = await _subscriber!.PublishAsync(
            new RedisChannel(channel, RedisChannel.PatternMode.Literal), message).ConfigureAwait(false);

        SetResult(exchange, recipients);
        exchange.In.Headers[RedisHeaders.PublishRecipients] = recipients;

        Logger?.LogDebug("Redis PUBLISH: channel={Channel}, recipients={Recipients}", channel, recipients);
    }

    // ═══════════════════════════════════════════════════════════
    // Streams
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessStreamAddAsync(IExchange exchange)
    {
        var streamName = _options.ResolveOption(_options.StreamName ?? _endpoint.Resource, exchange)
                         ?? _endpoint.Resource;
        var fields = GetStreamFields(exchange);
        var maxLength = _options.StreamMaxLength > 0 ? (int?)_options.StreamMaxLength : null;

        var messageId = await _db!.StreamAddAsync(
            streamName, fields.ToArray(),
            maxLength: maxLength,
            useApproximateMaxLength: _options.StreamApproximate).ConfigureAwait(false);

        SetResult(exchange, messageId.ToString());
        exchange.In.Headers[RedisHeaders.StreamMessageId] = messageId.ToString();

        Logger?.LogDebug("Redis XADD: stream={Stream}, messageId={Id}", streamName, messageId);
    }

    /// <summary>
    /// Coerces an exchange Body to a <see cref="RedisValue"/> suitable for Redis commands.
    /// Honours pre-marshalled <see cref="byte"/>[] (passed as binary RedisValue),
    /// strings, and falls back to <see cref="object.ToString"/> for arbitrary types.
    /// Avoids the previous <c>Body?.ToString()</c> trap that emitted <c>"System.Byte[]"</c>
    /// when an upstream <c>.Marshal()</c> step had already produced JSON bytes.
    /// </summary>
    private static RedisValue BodyToRedisValue(object? body) => body switch
    {
        null => RedisValue.EmptyString,
        byte[] arr => arr,
        string s => s,
        _ => body.ToString() ?? string.Empty
    };

    // ═══════════════════════════════════════════════════════════
    // Lists
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessListAsync(IExchange exchange)
    {
        var key = ResolveKey(exchange);

        switch (_endpoint.OperationType)
        {
            case RedisOperationType.LPUSH:
                var lpushLen = await _db!.ListLeftPushAsync(key, BodyToRedisValue(exchange.In.Body)).ConfigureAwait(false);
                SetResult(exchange, lpushLen);
                break;
            case RedisOperationType.RPUSH:
                var rpushLen = await _db!.ListRightPushAsync(key, BodyToRedisValue(exchange.In.Body)).ConfigureAwait(false);
                SetResult(exchange, rpushLen);
                break;
            case RedisOperationType.LPOP:
                var lpop = await _db!.ListLeftPopAsync(key).ConfigureAwait(false);
                SetResult(exchange, lpop.HasValue ? lpop.ToString() : null);
                break;
            case RedisOperationType.RPOP:
                var rpop = await _db!.ListRightPopAsync(key).ConfigureAwait(false);
                SetResult(exchange, rpop.HasValue ? rpop.ToString() : null);
                break;
            case RedisOperationType.LLEN:
                var len = await _db!.ListLengthAsync(key).ConfigureAwait(false);
                SetResult(exchange, len);
                break;
            case RedisOperationType.LRANGE:
                var start = _options.Start ?? 0;
                var stop = _options.Stop ?? -1;
                var range = await _db!.ListRangeAsync(key, start, stop).ConfigureAwait(false);
                SetResult(exchange, range.Select(v => v.ToString()).ToArray());
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Hashes
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessHashAsync(IExchange exchange)
    {
        var key = ResolveKey(exchange);

        switch (_endpoint.OperationType)
        {
            case RedisOperationType.HSET:
                var field = RequireField(exchange);
                var wasSet = await _db!.HashSetAsync(key, field, BodyToRedisValue(exchange.In.Body)).ConfigureAwait(false);
                SetResult(exchange, wasSet);
                break;
            case RedisOperationType.HGET:
                field = RequireField(exchange);
                var hgetResult = await _db!.HashGetAsync(key, field).ConfigureAwait(false);
                SetResult(exchange, hgetResult.HasValue ? hgetResult.ToString() : null);
                break;
            case RedisOperationType.HMSET:
                var hashFields = GetHashFields(exchange);
                await _db!.HashSetAsync(key, hashFields.ToArray()).ConfigureAwait(false);
                SetResult(exchange, "OK");
                break;
            case RedisOperationType.HMGET:
                var fieldNames = GetFieldNames(exchange);
                var hmgetResults = await _db!.HashGetAsync(key, fieldNames).ConfigureAwait(false);
                SetResult(exchange, hmgetResults.Select(v => v.HasValue ? v.ToString() : null).ToArray());
                break;
            case RedisOperationType.HGETALL:
                var all = await _db!.HashGetAllAsync(key).ConfigureAwait(false);
                SetResult(exchange, all.ToDictionary(h => h.Name.ToString(), h => (object?)h.Value.ToString()));
                break;
            case RedisOperationType.HDEL:
                field = RequireField(exchange);
                var hdel = await _db!.HashDeleteAsync(key, field).ConfigureAwait(false);
                SetResult(exchange, hdel);
                break;
            case RedisOperationType.HLEN:
                var hlen = await _db!.HashLengthAsync(key).ConfigureAwait(false);
                SetResult(exchange, hlen);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Sets
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessSetAsync(IExchange exchange)
    {
        var key = ResolveKey(exchange);

        switch (_endpoint.OperationType)
        {
            case RedisOperationType.SADD:
                var added = await _db!.SetAddAsync(key, BodyToRedisValue(exchange.In.Body)).ConfigureAwait(false);
                SetResult(exchange, added);
                break;
            case RedisOperationType.SREM:
                var removed = await _db!.SetRemoveAsync(key, BodyToRedisValue(exchange.In.Body)).ConfigureAwait(false);
                SetResult(exchange, removed);
                break;
            case RedisOperationType.SMEMBERS:
                var members = await _db!.SetMembersAsync(key).ConfigureAwait(false);
                SetResult(exchange, members.Select(m => m.ToString()).ToArray());
                break;
            case RedisOperationType.SCARD:
                var count = await _db!.SetLengthAsync(key).ConfigureAwait(false);
                SetResult(exchange, count);
                break;
            case RedisOperationType.SISMEMBER:
                var isMember = await _db!.SetContainsAsync(key, BodyToRedisValue(exchange.In.Body)).ConfigureAwait(false);
                SetResult(exchange, isMember);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Sorted Sets
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessSortedSetAsync(IExchange exchange)
    {
        var key = ResolveKey(exchange);

        switch (_endpoint.OperationType)
        {
            case RedisOperationType.ZADD:
                var score = _options.Score ?? throw new InvalidOperationException("Score is required for ZADD (use ?score=N).");
                var zadded = await _db!.SortedSetAddAsync(key, BodyToRedisValue(exchange.In.Body), score).ConfigureAwait(false);
                SetResult(exchange, zadded);
                break;
            case RedisOperationType.ZREM:
                var zremoved = await _db!.SortedSetRemoveAsync(key, BodyToRedisValue(exchange.In.Body)).ConfigureAwait(false);
                SetResult(exchange, zremoved);
                break;
            case RedisOperationType.ZRANGE:
                var start = _options.Start ?? 0;
                var stop = _options.Stop ?? -1;
                var zrange = await _db!.SortedSetRangeByRankAsync(key, start, stop).ConfigureAwait(false);
                SetResult(exchange, zrange.Select(v => v.ToString()).ToArray());
                break;
            case RedisOperationType.ZCARD:
                var zcard = await _db!.SortedSetLengthAsync(key).ConfigureAwait(false);
                SetResult(exchange, zcard);
                break;
            case RedisOperationType.ZSCORE:
                var zscore = await _db!.SortedSetScoreAsync(key, BodyToRedisValue(exchange.In.Body)).ConfigureAwait(false);
                SetResult(exchange, zscore);
                break;
            case RedisOperationType.ZRANGEBYSCORE:
                var min = _options.MinScore ?? throw new InvalidOperationException("MinScore required for ZRANGEBYSCORE.");
                var max = _options.MaxScore ?? throw new InvalidOperationException("MaxScore required for ZRANGEBYSCORE.");
                var byScore = await _db!.SortedSetRangeByScoreAsync(key, min, max).ConfigureAwait(false);
                SetResult(exchange, byScore.Select(v => v.ToString()).ToArray());
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Geospatial
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessGeoAsync(IExchange exchange)
    {
        var key = ResolveKey(exchange);

        switch (_endpoint.OperationType)
        {
            case RedisOperationType.GEOADD:
                var lon = _options.Longitude ?? throw new InvalidOperationException("Longitude required for GEOADD.");
                var lat = _options.Latitude ?? throw new InvalidOperationException("Latitude required for GEOADD.");
                var member = BodyToRedisValue(exchange.In.Body);
                var geoAdded = await _db!.GeoAddAsync(key, lon, lat, member).ConfigureAwait(false);
                SetResult(exchange, geoAdded);
                break;

            case RedisOperationType.GEODIST:
                var m1 = _options.Member1 ?? throw new InvalidOperationException("Member1 required for GEODIST.");
                var m2 = _options.Member2 ?? throw new InvalidOperationException("Member2 required for GEODIST.");
                var unit = ParseGeoUnit(_options.GeoUnit);
                var dist = await _db!.GeoDistanceAsync(key, m1, m2, unit).ConfigureAwait(false);
                SetResult(exchange, dist);
                break;

            case RedisOperationType.GEORADIUS:
                var cLon = exchange.In.GetHeader<double>(RedisHeaders.CenterLongitude);
                var cLat = exchange.In.GetHeader<double>(RedisHeaders.CenterLatitude);
                var radius = exchange.In.GetHeader<double>(RedisHeaders.Radius);
                var rUnit = ParseGeoUnit(exchange.In.GetHeader<string>(RedisHeaders.RadiusUnit) ?? "m");
                var geoResults = await _db!.GeoRadiusAsync(key, cLon, cLat, radius, rUnit).ConfigureAwait(false);
                SetResult(exchange, geoResults.Select(r => r.Member.ToString()).ToArray());
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // HyperLogLog
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessHyperLogLogAsync(IExchange exchange)
    {
        var key = ResolveKey(exchange);

        switch (_endpoint.OperationType)
        {
            case RedisOperationType.PFADD:
                var added = await _db!.HyperLogLogAddAsync(key, BodyToRedisValue(exchange.In.Body)).ConfigureAwait(false);
                SetResult(exchange, added);
                break;
            case RedisOperationType.PFCOUNT:
                var count = await _db!.HyperLogLogLengthAsync(key).ConfigureAwait(false);
                SetResult(exchange, count);
                break;
            case RedisOperationType.PFMERGE:
                var sourceKeys = exchange.In.GetHeader<string[]>(RedisHeaders.SourceKeys)
                    ?? throw new InvalidOperationException("SourceKeys header required for PFMERGE.");
                await _db!.HyperLogLogMergeAsync(key, sourceKeys.Select(k => (RedisKey)k).ToArray()).ConfigureAwait(false);
                SetResult(exchange, "OK");
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Bitmap
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessBitmapAsync(IExchange exchange)
    {
        var key = ResolveKey(exchange);

        switch (_endpoint.OperationType)
        {
            case RedisOperationType.SETBIT:
                var offset = _options.Offset ?? exchange.In.GetHeader<long>(RedisHeaders.BitOffset);
                var bit = _options.Bit ?? exchange.In.GetHeader<bool>(RedisHeaders.Bit);
                var oldBit = await _db!.StringSetBitAsync(key, offset, bit).ConfigureAwait(false);
                SetResult(exchange, oldBit);
                break;
            case RedisOperationType.GETBIT:
                offset = _options.Offset ?? exchange.In.GetHeader<long>(RedisHeaders.BitOffset);
                var getBit = await _db!.StringGetBitAsync(key, offset).ConfigureAwait(false);
                SetResult(exchange, getBit);
                break;
            case RedisOperationType.BITCOUNT:
                var bitCount = await _db!.StringBitCountAsync(key).ConfigureAwait(false);
                SetResult(exchange, bitCount);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Command (arbitrary)
    // ═══════════════════════════════════════════════════════════

    private async Task ProcessCommandAsync(IExchange exchange)
    {
        var command = _options.Command ?? exchange.In.GetHeader<string>(RedisHeaders.Command)
            ?? throw new InvalidOperationException("Command is required for COMMAND operation.");

        var args = GetCommandArguments(exchange);
        var result = await _db!.ExecuteAsync(command, args).ConfigureAwait(false);
        SetResult(exchange, result?.ToString());
    }

    // ═══════════════════════════════════════════════════════════
    // Transactional (deferred write)
    // ═══════════════════════════════════════════════════════════

    private void ProcessTransactional(IExchange exchange)
    {
        // Capture state — resolve dynamic values NOW, exchange won't be available at commit
        var db = _db!;
        var op = _endpoint.OperationType;
        var key = ResolveKey(exchange);
        var bodyText = exchange.In.Body?.ToString() ?? string.Empty;
        string? field = _options.Field != null ? _options.ResolveOption(_options.Field, exchange) : null;

        var action = new RedisWriteAction(db, op, key, bodyText, field, _options.Score, Logger);
        RegisterTransactedAction(exchange, $"redis-write-{Guid.NewGuid():N}", action);
    }

    // ═══════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════

    private string ResolveKey(IExchange exchange)
    {
        var raw = _options.Key ?? _endpoint.Resource;
        if (string.IsNullOrEmpty(raw))
            throw new InvalidOperationException("Redis key is required but not set in URI or options.");
        return _options.ResolveOption(raw, exchange) ?? raw;
    }

    private string RequireField(IExchange exchange)
    {
        var raw = _options.Field
            ?? throw new InvalidOperationException("Field is required (use ?field=name in URI).");
        return _options.ResolveOption(raw, exchange) ?? raw;
    }

    private static void SetResult(IExchange exchange, object? value)
    {
        exchange.Out ??= new Message();
        exchange.Out.Body = value;
        exchange.Pattern = ExchangePattern.InOut;
    }

    private List<NameValueEntry> GetStreamFields(IExchange exchange)
    {
        // Check for StreamFields header (Dictionary<string, object>) — prefixed name, bare accepted for compat
        if ((exchange.In.Headers.TryGetValue(RedisHeaders.StreamFields, out var raw) || exchange.In.Headers.TryGetValue("StreamFields", out raw))
            && raw is Dictionary<string, object> dict)
        {
            return dict.Select(kv => new NameValueEntry(kv.Key, kv.Value?.ToString() ?? "")).ToList();
        }

        // Default: body as "data" field
        return new List<NameValueEntry>
        {
            new("data", BodyToRedisValue(exchange.In.Body)),
            new("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString())
        };
    }

    private List<HashEntry> GetHashFields(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(RedisHeaders.HashFields, out var raw) && raw is Dictionary<string, object> dict)
        {
            return dict.Select(kv => new HashEntry(kv.Key, kv.Value?.ToString() ?? "")).ToList();
        }

        var field = RequireField(exchange);
        return new List<HashEntry> { new(field, BodyToRedisValue(exchange.In.Body)) };
    }

    private RedisValue[] GetFieldNames(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(RedisHeaders.FieldNames, out var raw) && raw is string[] names)
            return names.Select(f => (RedisValue)f).ToArray();
        return new RedisValue[] { RequireField(exchange) };
    }

    private static object[] GetCommandArguments(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(RedisHeaders.CommandArgs, out var raw) && raw is string[] args)
            return args.Cast<object>().ToArray();

        return new object[] { BodyToRedisValue(exchange.In.Body) };
    }

    private static GeoUnit ParseGeoUnit(string unit) => unit.ToLowerInvariant() switch
    {
        "km" => GeoUnit.Kilometers,
        "mi" => GeoUnit.Miles,
        "ft" => GeoUnit.Feet,
        _ => GeoUnit.Meters
    };

    private static bool IsWriteOperation(RedisOperationType op) => op switch
    {
        RedisOperationType.GET or RedisOperationType.EXISTS or RedisOperationType.LLEN or
        RedisOperationType.LRANGE or RedisOperationType.HGET or RedisOperationType.HMGET or
        RedisOperationType.HGETALL or RedisOperationType.HLEN or RedisOperationType.SMEMBERS or
        RedisOperationType.SCARD or RedisOperationType.SISMEMBER or RedisOperationType.ZRANGE or
        RedisOperationType.ZCARD or RedisOperationType.ZSCORE or RedisOperationType.ZRANGEBYSCORE or
        RedisOperationType.GEODIST or RedisOperationType.GEORADIUS or RedisOperationType.PFCOUNT or
        RedisOperationType.GETBIT or RedisOperationType.BITCOUNT => false,
        _ => true
    };

    private static void RegisterTransactedAction(IExchange exchange, string key, ITransactedAction action)
    {
        if (!exchange.Properties.TryGetValue("TRANSACT_ACTION", out var raw) ||
            raw is not ConcurrentDictionary<string, ITransactedAction> dict)
        {
            dict = new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase);
            exchange.Properties["TRANSACT_ACTION"] = dict;
        }

        dict[key] = action;
    }
}

/// <summary>
/// Deferred Redis write action. Executes the write on commit; discards on rollback.
/// </summary>
internal sealed class RedisWriteAction : ITransactedAction
{
    private readonly IDatabase _db;
    private readonly RedisOperationType _op;
    private readonly string _key;
    private readonly string _body;
    private readonly string? _field;
    private readonly double? _score;
    private readonly ILogger? _logger;

    public RedisWriteAction(
        IDatabase db, RedisOperationType op, string key, string body,
        string? field, double? score, ILogger? logger)
    {
        _db = db;
        _op = op;
        _key = key;
        _body = body;
        _field = field;
        _score = score;
        _logger = logger;
    }

    public async Task Commit(CancellationToken ct = default)
    {
        switch (_op)
        {
            case RedisOperationType.SET:
                await _db.StringSetAsync(_key, _body).ConfigureAwait(false);
                break;
            case RedisOperationType.SETNX:
                await _db.StringSetAsync(_key, _body, when: When.NotExists).ConfigureAwait(false);
                break;
            case RedisOperationType.DEL:
                await _db.KeyDeleteAsync(_key).ConfigureAwait(false);
                break;
            case RedisOperationType.INCR:
                await _db.StringIncrementAsync(_key).ConfigureAwait(false);
                break;
            case RedisOperationType.DECR:
                await _db.StringDecrementAsync(_key).ConfigureAwait(false);
                break;
            case RedisOperationType.EXPIRE:
                await _db.KeyExpireAsync(_key, TimeSpan.FromHours(1)).ConfigureAwait(false);
                break;
            case RedisOperationType.LPUSH:
                await _db.ListLeftPushAsync(_key, _body).ConfigureAwait(false);
                break;
            case RedisOperationType.RPUSH:
                await _db.ListRightPushAsync(_key, _body).ConfigureAwait(false);
                break;
            case RedisOperationType.PUBLISH:
                await _db.PublishAsync(new RedisChannel(_key, RedisChannel.PatternMode.Literal), _body).ConfigureAwait(false);
                break;
            case RedisOperationType.SADD:
                await _db.SetAddAsync(_key, _body).ConfigureAwait(false);
                break;
            case RedisOperationType.SREM:
                await _db.SetRemoveAsync(_key, _body).ConfigureAwait(false);
                break;
            case RedisOperationType.HSET:
                var field = _field ?? throw new InvalidOperationException("Field required for transacted HSET.");
                await _db.HashSetAsync(_key, field, _body).ConfigureAwait(false);
                break;
            case RedisOperationType.HDEL:
                field = _field ?? throw new InvalidOperationException("Field required for transacted HDEL.");
                await _db.HashDeleteAsync(_key, field).ConfigureAwait(false);
                break;
            case RedisOperationType.ZADD:
                var score = _score ?? throw new InvalidOperationException("Score required for transacted ZADD.");
                await _db.SortedSetAddAsync(_key, _body, score).ConfigureAwait(false);
                break;
            case RedisOperationType.ZREM:
                await _db.SortedSetRemoveAsync(_key, _body).ConfigureAwait(false);
                break;
            default:
                _logger?.LogWarning("Transacted commit for {Op} not implemented — executing as no-op", _op);
                break;
        }

        _logger?.LogDebug("Redis transacted write committed: {Op} {Key}", _op, _key);
    }

    public Task Rollback(CancellationToken ct = default)
    {
        _logger?.LogDebug("Redis transacted write rolled back: {Op} {Key}", _op, _key);
        return Task.CompletedTask;
    }
}
