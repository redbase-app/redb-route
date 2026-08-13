using System.Text;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Redis;

/// <summary>
/// Fluent API for Redis endpoints.
/// <example><code>
/// // Key-value
/// .To(Redis.Set("user:123").Connection("redis-server:6379").Ttl(300))
/// .From(Redis.Subscribe("events.*").UsePattern())
///
/// // Streams
/// .From(Redis.XRead("order-stream").ConsumerGroup("workers").ConsumerName("w1"))
/// .To(Redis.XAdd("audit-stream").StreamMaxLength(10000))
///
/// // Generic operation
/// .To(Redis.Command("LPUSH", "task-queue"))
/// </code></example>
/// </summary>
public static class Redis
{
    // ── Key-Value ─────────────────────────────────────────────────────

    /// <summary>SET key to a value.</summary>
    public static RedisBuilder Set(string key) => new("SET", key);

    /// <summary>GET value by key.</summary>
    public static RedisBuilder Get(string key) => new("GET", key);

    /// <summary>DEL — delete a key.</summary>
    public static RedisBuilder Del(string key) => new("DEL", key);

    /// <summary>EXISTS — check if key exists.</summary>
    public static RedisBuilder Exists(string key) => new("EXISTS", key);

    /// <summary>EXPIRE — set TTL on a key.</summary>
    public static RedisBuilder Expire(string key) => new("EXPIRE", key);

    /// <summary>INCR — increment a key.</summary>
    public static RedisBuilder Incr(string key) => new("INCR", key);

    /// <summary>DECR — decrement a key.</summary>
    public static RedisBuilder Decr(string key) => new("DECR", key);

    /// <summary>SETNX — set value only if key does not exist.</summary>
    public static RedisBuilder SetNx(string key) => new("SETNX", key);

    // ── Pub/Sub ───────────────────────────────────────────────────────

    /// <summary>PUBLISH messages to a channel.</summary>
    public static RedisBuilder Publish(string channel) => new("PUBLISH", channel);

    /// <summary>SUBSCRIBE to a channel (consumer).</summary>
    public static RedisBuilder Subscribe(string channel) => new("SUBSCRIBE", channel);

    /// <summary>PSUBSCRIBE to a pattern (consumer).</summary>
    public static RedisBuilder PSubscribe(string pattern) => new("PSUBSCRIBE", pattern);

    // ── Streams ───────────────────────────────────────────────────────

    /// <summary>XADD — write to a stream.</summary>
    public static RedisBuilder XAdd(string stream) => new("XADD", stream);

    /// <summary>XREAD — read from a stream (consumer).</summary>
    public static RedisBuilder XRead(string stream) => new("XREAD", stream);

    /// <summary>XGROUP — manage consumer groups.</summary>
    public static RedisBuilder XGroup(string stream) => new("XGROUP", stream);

    // ── Lists ─────────────────────────────────────────────────────────

    /// <summary>LPUSH — push to head of list.</summary>
    public static RedisBuilder LPush(string key) => new("LPUSH", key);

    /// <summary>RPUSH — push to tail of list.</summary>
    public static RedisBuilder RPush(string key) => new("RPUSH", key);

    /// <summary>LPOP — pop from head of list.</summary>
    public static RedisBuilder LPop(string key) => new("LPOP", key);

    /// <summary>RPOP — pop from tail of list.</summary>
    public static RedisBuilder RPop(string key) => new("RPOP", key);

    /// <summary>LLEN — get list length.</summary>
    public static RedisBuilder LLen(string key) => new("LLEN", key);

    /// <summary>LRANGE — get range from list.</summary>
    public static RedisBuilder LRange(string key) => new("LRANGE", key);

    // ── Generic ───────────────────────────────────────────────────────

    /// <summary>Any Redis operation by name.</summary>
    public static RedisBuilder Command(string operation, string key) => new(operation, key);
}

/// <summary>
/// Fluent builder for Redis endpoint URIs.
/// </summary>
public sealed class RedisBuilder
{
    private readonly string _operation;
    private readonly string _resource;

    // Connection
    private string? _connectionString;
    private string? _database;
    private string? _password;
    private string? _connectionFactory;

    // Common
    private string? _ttl;
    private bool _usePattern;
    private bool _transacted;
    private string? _pollDelayMs;
    private string? _command;

    // Sorted set
    private string? _score;
    private string? _minScore;
    private string? _maxScore;

    // Hash
    private string? _field;

    // Range
    private string? _start;
    private string? _stop;

    // Geo
    private string? _longitude;
    private string? _latitude;
    private string? _member1;
    private string? _member2;
    private string? _geoUnit;

    // Bit
    private string? _offset;
    private bool? _bit;

    // Stream
    private string? _consumerGroup;
    private string? _consumerName;
    private string? _streamMaxLength;
    private bool? _streamApproximate;
    private string? _streamReadCount;
    private string? _streamBlockTimeMs;
    private bool? _streamAutoAck;
    private string? _streamStartPosition;

    internal RedisBuilder(string operation, string resource)
    {
        _operation = operation;
        _resource = resource;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Redis connection string. Default "localhost:6379".</summary>
    public RedisBuilder Connection(IExpression connectionString) { _connectionString = connectionString.ToTemplateString(); return this; }
    /// <summary>Redis connection string (template string, supports ${...}). Default "localhost:6379".</summary>
    public RedisBuilder Connection(string connectionString) => Connection(new StringExpression(connectionString));

    /// <summary>Database number. Default 0.</summary>
    public RedisBuilder Database(int db) { _database = db.ToString(); return this; }
    /// <summary>Database number from an expression.</summary>
    public RedisBuilder Database(IExpression db) { _database = db.ToTemplateString(); return this; }

    /// <summary>Redis password.</summary>
    public RedisBuilder Password(IExpression password) { _password = password.ToTemplateString(); return this; }
    /// <summary>Redis password (template string, supports ${...}).</summary>
    public RedisBuilder Password(string password) => Password(new StringExpression(password));

    /// <summary>Use a named connection factory registered in DI.</summary>
    public RedisBuilder ConnectionFactory(IExpression name) { _connectionFactory = name.ToTemplateString(); return this; }
    /// <summary>Use a named connection factory registered in DI (template string, supports ${...}).</summary>
    public RedisBuilder ConnectionFactory(string name) => ConnectionFactory(new StringExpression(name));

    // ── Common ────────────────────────────────────────────────────────

    /// <summary>TTL for SET operations in seconds. Default 0 (no expiry).</summary>
    public RedisBuilder Ttl(int seconds) { _ttl = seconds.ToString(); return this; }
    /// <summary>TTL from an expression.</summary>
    public RedisBuilder Ttl(IExpression seconds) { _ttl = seconds.ToTemplateString(); return this; }

    /// <summary>Use pattern matching for SUBSCRIBE/key operations.</summary>
    public RedisBuilder UsePattern() { _usePattern = true; return this; }

    /// <summary>Enable transacted (pipeline) mode.</summary>
    public RedisBuilder Transacted() { _transacted = true; return this; }

    /// <summary>Poll delay for consumer operations in milliseconds. Default 1000.</summary>
    public RedisBuilder PollDelay(int ms) { _pollDelayMs = ms.ToString(); return this; }
    /// <summary>Poll delay from an expression.</summary>
    public RedisBuilder PollDelay(IExpression ms) { _pollDelayMs = ms.ToTemplateString(); return this; }

    /// <summary>Custom Redis command string.</summary>
    public RedisBuilder CustomCommand(string cmd) { _command = cmd; return this; }

    // ── Sorted Set ────────────────────────────────────────────────────

    /// <summary>Score for sorted set operations.</summary>
    public RedisBuilder Score(double score) { _score = score.ToString(System.Globalization.CultureInfo.InvariantCulture); return this; }
    /// <summary>Score from an expression.</summary>
    public RedisBuilder Score(IExpression score) { _score = score.ToTemplateString(); return this; }

    /// <summary>Min score for range queries.</summary>
    public RedisBuilder MinScore(double min) { _minScore = min.ToString(System.Globalization.CultureInfo.InvariantCulture); return this; }
    /// <summary>Min score from an expression.</summary>
    public RedisBuilder MinScore(IExpression min) { _minScore = min.ToTemplateString(); return this; }

    /// <summary>Max score for range queries.</summary>
    public RedisBuilder MaxScore(double max) { _maxScore = max.ToString(System.Globalization.CultureInfo.InvariantCulture); return this; }
    /// <summary>Max score from an expression.</summary>
    public RedisBuilder MaxScore(IExpression max) { _maxScore = max.ToTemplateString(); return this; }

    // ── Hash ──────────────────────────────────────────────────────────

    /// <summary>Hash field name.</summary>
    public RedisBuilder Field(IExpression field) { _field = field.ToTemplateString(); return this; }
    /// <summary>Hash field name (template string, supports ${...}).</summary>
    public RedisBuilder Field(string field) => Field(new StringExpression(field));

    // ── Range ─────────────────────────────────────────────────────────

    /// <summary>Start index for list/range operations.</summary>
    public RedisBuilder Start(long start) { _start = start.ToString(); return this; }
    /// <summary>Start index from an expression.</summary>
    public RedisBuilder Start(IExpression start) { _start = start.ToTemplateString(); return this; }

    /// <summary>Stop index for list/range operations.</summary>
    public RedisBuilder Stop(long stop) { _stop = stop.ToString(); return this; }
    /// <summary>Stop index from an expression.</summary>
    public RedisBuilder Stop(IExpression stop) { _stop = stop.ToTemplateString(); return this; }

    // ── Geo ───────────────────────────────────────────────────────────

    /// <summary>Longitude for geo operations.</summary>
    public RedisBuilder Longitude(double lon) { _longitude = lon.ToString(System.Globalization.CultureInfo.InvariantCulture); return this; }
    /// <summary>Longitude from an expression.</summary>
    public RedisBuilder Longitude(IExpression lon) { _longitude = lon.ToTemplateString(); return this; }

    /// <summary>Latitude for geo operations.</summary>
    public RedisBuilder Latitude(double lat) { _latitude = lat.ToString(System.Globalization.CultureInfo.InvariantCulture); return this; }
    /// <summary>Latitude from an expression.</summary>
    public RedisBuilder Latitude(IExpression lat) { _latitude = lat.ToTemplateString(); return this; }

    /// <summary>First member for geo distance.</summary>
    public RedisBuilder Member1(IExpression member) { _member1 = member.ToTemplateString(); return this; }
    /// <summary>First member for geo distance (template string, supports ${...}).</summary>
    public RedisBuilder Member1(string member) => Member1(new StringExpression(member));

    /// <summary>Second member for geo distance.</summary>
    public RedisBuilder Member2(IExpression member) { _member2 = member.ToTemplateString(); return this; }
    /// <summary>Second member for geo distance (template string, supports ${...}).</summary>
    public RedisBuilder Member2(string member) => Member2(new StringExpression(member));

    /// <summary>Geo distance unit: m, km, mi, ft. Default "m".</summary>
    public RedisBuilder GeoUnit(string unit) { _geoUnit = unit; return this; }

    // ── Bit ───────────────────────────────────────────────────────────

    /// <summary>Bit offset for GETBIT/SETBIT.</summary>
    public RedisBuilder Offset(long offset) { _offset = offset.ToString(); return this; }
    /// <summary>Bit offset from an expression.</summary>
    public RedisBuilder Offset(IExpression offset) { _offset = offset.ToTemplateString(); return this; }

    /// <summary>Bit value for SETBIT.</summary>
    public RedisBuilder Bit(bool value) { _bit = value; return this; }

    // ── Stream ────────────────────────────────────────────────────────

    /// <summary>Consumer group name for stream operations.</summary>
    public RedisBuilder ConsumerGroup(IExpression group) { _consumerGroup = group.ToTemplateString(); return this; }
    /// <summary>Consumer group name for stream operations (template string, supports ${...}).</summary>
    public RedisBuilder ConsumerGroup(string group) => ConsumerGroup(new StringExpression(group));

    /// <summary>Consumer name within the group.</summary>
    public RedisBuilder ConsumerName(IExpression name) { _consumerName = name.ToTemplateString(); return this; }
    /// <summary>Consumer name within the group (template string, supports ${...}).</summary>
    public RedisBuilder ConsumerName(string name) => ConsumerName(new StringExpression(name));

    /// <summary>Max stream length (XADD with MAXLEN). Default 0 (unlimited).</summary>
    public RedisBuilder StreamMaxLength(int max) { _streamMaxLength = max.ToString(); return this; }
    /// <summary>Max stream length from an expression.</summary>
    public RedisBuilder StreamMaxLength(IExpression max) { _streamMaxLength = max.ToTemplateString(); return this; }

    /// <summary>Approximate MAXLEN trimming. Default true.</summary>
    public RedisBuilder StreamApproximate(bool approx = true) { _streamApproximate = approx; return this; }

    /// <summary>Number of entries to read per XREAD. Default 10.</summary>
    public RedisBuilder StreamReadCount(int count) { _streamReadCount = count.ToString(); return this; }
    /// <summary>Stream read count from an expression.</summary>
    public RedisBuilder StreamReadCount(IExpression count) { _streamReadCount = count.ToTemplateString(); return this; }

    /// <summary>Block time in milliseconds for XREAD. Default 1000.</summary>
    public RedisBuilder StreamBlockTime(int ms) { _streamBlockTimeMs = ms.ToString(); return this; }
    /// <summary>Stream block time from an expression.</summary>
    public RedisBuilder StreamBlockTime(IExpression ms) { _streamBlockTimeMs = ms.ToTemplateString(); return this; }

    /// <summary>Auto-acknowledge stream messages. Default true.</summary>
    public RedisBuilder StreamAutoAck(bool ack = true) { _streamAutoAck = ack; return this; }

    /// <summary>Starting position for XREAD: "&gt;" (new), "0" (all), or specific ID.</summary>
    public RedisBuilder StreamStartPosition(string position) { _streamStartPosition = position; return this; }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the Redis endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("redis:");
        sb.Append(_operation);
        sb.Append(':');
        sb.Append(_resource);

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(Uri.EscapeDataString(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (value != null) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value)
        {
            if (value.HasValue) Append(key, value.Value.ToString().ToLowerInvariant());
        }

        // Connection
        AppendIf("connectionString", _connectionString);
        AppendIf("database", _database);
        AppendIf("password", _password);
        AppendIf("connectionFactory", _connectionFactory);

        // Common
        AppendIf("ttl", _ttl);
        AppendBool("usePattern", _usePattern);
        AppendBool("transacted", _transacted);
        AppendIf("pollDelayMs", _pollDelayMs);
        AppendIf("command", _command);

        // Sorted set
        AppendIf("score", _score);
        AppendIf("minScore", _minScore);
        AppendIf("maxScore", _maxScore);

        // Hash
        AppendIf("field", _field);

        // Range
        AppendIf("start", _start);
        AppendIf("stop", _stop);

        // Geo
        AppendIf("longitude", _longitude);
        AppendIf("latitude", _latitude);
        AppendIf("member1", _member1);
        AppendIf("member2", _member2);
        AppendIf("geoUnit", _geoUnit);

        // Bit
        AppendIf("offset", _offset);
        AppendBoolExplicit("bit", _bit);

        // Stream
        AppendIf("consumerGroup", _consumerGroup);
        AppendIf("consumerName", _consumerName);
        AppendIf("streamMaxLength", _streamMaxLength);
        AppendBoolExplicit("streamApproximate", _streamApproximate);
        AppendIf("streamReadCount", _streamReadCount);
        AppendIf("streamBlockTimeMs", _streamBlockTimeMs);
        AppendBoolExplicit("streamAutoAck", _streamAutoAck);
        AppendIf("streamStartPosition", _streamStartPosition);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(RedisBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
