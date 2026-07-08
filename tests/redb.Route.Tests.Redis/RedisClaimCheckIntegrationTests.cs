using System.Text;
using FluentAssertions;
using redb.Route.Redis;
using redb.Route.Redis.Repositories;
using Xunit.Abstractions;

namespace redb.Route.Tests.Redis;

/// <summary>
/// Integration tests for <see cref="RedisClaimCheckRepository"/> against a real Redis instance.
/// Expects Redis at localhost:6379 (no password).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisClaimCheckIntegrationTests : IAsyncDisposable
{
    private const string ConnectionString = "localhost:6379";
    private readonly ITestOutputHelper _output;
    private readonly RedisClaimCheckRepository _repo;
    private readonly string _prefix;

    public RedisClaimCheckIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _prefix = $"redb:test:claimcheck:{Guid.NewGuid():N}:";
        _repo = new RedisClaimCheckRepository(
            new RedisConnectionFactory { ConnectionString = ConnectionString },
            keyPrefix: _prefix,
            defaultTtl: TimeSpan.FromMinutes(5));
    }

    public async ValueTask DisposeAsync()
    {
        // Clean up test keys
        try
        {
            var conn = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(ConnectionString);
            var db = conn.GetDatabase();
            var server = conn.GetServer(ConnectionString);
            await foreach (var key in server.KeysAsync(pattern: $"{_prefix}*"))
                await db.KeyDeleteAsync(key);
            conn.Dispose();
        }
        catch { /* best effort cleanup */ }

        _repo.Dispose();
    }

    // ── Store / Retrieve ─────────────────────────────────────────

    [Fact]
    public async Task Store_ReturnsUniqueKey()
    {
        var data = Encoding.UTF8.GetBytes("redis-payload");

        var key1 = await _repo.Store(data);
        var key2 = await _repo.Store(data);

        key1.Should().NotBeNullOrEmpty();
        key2.Should().NotBeNullOrEmpty();
        key1.Should().NotBe(key2);
        _output.WriteLine($"Generated keys: {key1}, {key2}");
    }

    [Fact]
    public async Task Store_And_Retrieve_Roundtrip()
    {
        var data = Encoding.UTF8.GetBytes("test payload for redis");

        var key = await _repo.Store(data);
        var retrieved = await _repo.Retrieve(key);

        retrieved.Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Store_ExplicitKey_Roundtrip()
    {
        var data = Encoding.UTF8.GetBytes("explicit key data");

        await _repo.Store("mykey", data);
        var retrieved = await _repo.Retrieve("mykey");

        retrieved.Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Store_ExplicitKey_Overwrites()
    {
        await _repo.Store("overwrite", Encoding.UTF8.GetBytes("first"));
        await _repo.Store("overwrite", Encoding.UTF8.GetBytes("second"));

        var retrieved = await _repo.Retrieve("overwrite");
        Encoding.UTF8.GetString(retrieved!).Should().Be("second");
    }

    // ── RetrieveAndRemove ────────────────────────────────────────

    [Fact]
    public async Task RetrieveAndRemove_ReturnsDataAndDeletes()
    {
        var data = Encoding.UTF8.GetBytes("remove me");
        var key = await _repo.Store(data);

        var retrieved = await _repo.RetrieveAndRemove(key);
        retrieved.Should().BeEquivalentTo(data);

        var second = await _repo.Retrieve(key);
        second.Should().BeNull();
    }

    [Fact]
    public async Task RetrieveAndRemove_Missing_ReturnsNull()
    {
        var result = await _repo.RetrieveAndRemove("nonexistent-key");
        result.Should().BeNull();
    }

    // ── Remove ───────────────────────────────────────────────────

    [Fact]
    public async Task Remove_DeletesEntry()
    {
        var key = await _repo.Store(Encoding.UTF8.GetBytes("to delete"));
        await _repo.Remove(key);

        (await _repo.Retrieve(key)).Should().BeNull();
    }

    // ── TTL ──────────────────────────────────────────────────────

    [Fact]
    public async Task Store_WithTtl_ExpiresNatively()
    {
        // Use very short TTL
        var shortTtlRepo = new RedisClaimCheckRepository(
            new RedisConnectionFactory { ConnectionString = ConnectionString },
            keyPrefix: _prefix + "ttl:",
            defaultTtl: TimeSpan.FromSeconds(1));

        var key = await shortTtlRepo.Store(Encoding.UTF8.GetBytes("expires soon"));
        (await shortTtlRepo.Retrieve(key)).Should().NotBeNull();

        // Wait for Redis TTL to expire
        await Task.Delay(1500);

        (await shortTtlRepo.Retrieve(key)).Should().BeNull();
        shortTtlRepo.Dispose();
    }

    // ── Binary data ──────────────────────────────────────────────

    [Fact]
    public async Task Store_BinaryData_Roundtrip()
    {
        var data = new byte[] { 0x00, 0xFF, 0x42, 0x13, 0x80, 0xFE };

        var key = await _repo.Store(data);
        var retrieved = await _repo.Retrieve(key);

        retrieved.Should().BeEquivalentTo(data);
    }

    // ── Large payload ────────────────────────────────────────────

    [Fact]
    public async Task Store_LargePayload_Roundtrip()
    {
        var data = new byte[1024 * 1024]; // 1 MB
        new Random(42).NextBytes(data);

        var key = await _repo.Store(data);
        var retrieved = await _repo.Retrieve(key);

        retrieved.Should().BeEquivalentTo(data);
        _output.WriteLine("1 MB payload stored and retrieved successfully");
    }
}
