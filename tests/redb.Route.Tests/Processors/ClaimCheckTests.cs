using System.Text;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Unit tests for <see cref="InMemoryClaimCheckRepository"/> and <see cref="ClaimCheckProcessor"/>.
/// </summary>
public class ClaimCheckTests
{
    // ══════════════════════════════════════════════════════════════
    // InMemoryClaimCheckRepository
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Repository_Store_ReturnsUniqueKey()
    {
        var repo = new InMemoryClaimCheckRepository();
        var data = Encoding.UTF8.GetBytes("hello");

        var key1 = await repo.Store(data);
        var key2 = await repo.Store(data);

        key1.Should().NotBeNullOrEmpty();
        key2.Should().NotBeNullOrEmpty();
        key1.Should().NotBe(key2);
    }

    [Fact]
    public async Task Repository_StoreAndRetrieve_Roundtrip()
    {
        var repo = new InMemoryClaimCheckRepository();
        var data = Encoding.UTF8.GetBytes("test payload");

        var key = await repo.Store(data);
        var retrieved = await repo.Retrieve(key);

        retrieved.Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Repository_StoreWithExplicitKey_Roundtrip()
    {
        var repo = new InMemoryClaimCheckRepository();
        var data = Encoding.UTF8.GetBytes("keyed data");

        await repo.Store("my-key", data);
        var retrieved = await repo.Retrieve("my-key");

        retrieved.Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Repository_StoreWithExplicitKey_Overwrites()
    {
        var repo = new InMemoryClaimCheckRepository();

        await repo.Store("same-key", Encoding.UTF8.GetBytes("first"));
        await repo.Store("same-key", Encoding.UTF8.GetBytes("second"));

        var retrieved = await repo.Retrieve("same-key");
        Encoding.UTF8.GetString(retrieved!).Should().Be("second");
    }

    [Fact]
    public async Task Repository_Retrieve_ReturnsNull_ForMissingKey()
    {
        var repo = new InMemoryClaimCheckRepository();
        var result = await repo.Retrieve("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Repository_RetrieveAndRemove_RemovesEntry()
    {
        var repo = new InMemoryClaimCheckRepository();
        var key = await repo.Store(Encoding.UTF8.GetBytes("temp"));

        var data = await repo.RetrieveAndRemove(key);
        data.Should().NotBeNull();

        var second = await repo.Retrieve(key);
        second.Should().BeNull();
    }

    [Fact]
    public async Task Repository_Remove_DeletesEntry()
    {
        var repo = new InMemoryClaimCheckRepository();
        var key = await repo.Store(Encoding.UTF8.GetBytes("remove me"));

        await repo.Remove(key);

        var result = await repo.Retrieve(key);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Repository_Remove_NoOpForMissingKey()
    {
        var repo = new InMemoryClaimCheckRepository();
        var act = async () => await repo.Remove("missing");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Repository_WithTtl_ReturnsNull_AfterExpiry()
    {
        var repo = new InMemoryClaimCheckRepository(TimeSpan.FromMilliseconds(50));
        var key = await repo.Store(Encoding.UTF8.GetBytes("ttl data"));

        (await repo.Retrieve(key)).Should().NotBeNull();

        await Task.Delay(80);

        (await repo.Retrieve(key)).Should().BeNull();
    }

    [Fact]
    public async Task Repository_WithTtl_RetrieveAndRemove_ReturnsNull_AfterExpiry()
    {
        var repo = new InMemoryClaimCheckRepository(TimeSpan.FromMilliseconds(50));
        var key = await repo.Store(Encoding.UTF8.GetBytes("ttl data"));

        await Task.Delay(80);

        (await repo.RetrieveAndRemove(key)).Should().BeNull();
    }

    [Fact]
    public async Task Repository_ExplicitTtl_OverridesDefault()
    {
        var repo = new InMemoryClaimCheckRepository(TimeSpan.FromSeconds(30));
        var key = await repo.Store(Encoding.UTF8.GetBytes("short"), ttl: TimeSpan.FromMilliseconds(50));

        await Task.Delay(80);

        (await repo.Retrieve(key)).Should().BeNull();
    }

    [Fact]
    public async Task Repository_NullKey_Throws()
    {
        var repo = new InMemoryClaimCheckRepository();
        var act = async () => await repo.Retrieve(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Repository_Count_TracksEntries()
    {
        var repo = new InMemoryClaimCheckRepository();
        repo.Count.Should().Be(0);

        await repo.Store(Encoding.UTF8.GetBytes("a"));
        await repo.Store(Encoding.UTF8.GetBytes("b"));
        repo.Count.Should().Be(2);
    }

    // ══════════════════════════════════════════════════════════════
    // ClaimCheckProcessor — Set / Get / GetAndRemove
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Processor_Ctor_ThrowsOnNullRepository()
    {
        var act = () => new ClaimCheckProcessor(null!, ClaimCheckOperation.Set);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Processor_Set_ReplacesBodyWithKey()
    {
        var repo = new InMemoryClaimCheckRepository();
        var processor = new ClaimCheckProcessor(repo, ClaimCheckOperation.Set, "mykey");

        var exchange = new Exchange(new Message { Body = "large payload" });
        await processor.Process(exchange);

        exchange.In.Body.Should().Be("mykey");
        exchange.In.Headers[ClaimCheckHeaders.Key].Should().Be("mykey");
        repo.Count.Should().Be(1);
    }

    [Fact]
    public async Task Processor_Set_SavesOriginalBodyType()
    {
        var repo = new InMemoryClaimCheckRepository();
        var processor = new ClaimCheckProcessor(repo, ClaimCheckOperation.Set, "k1");

        var exchange = new Exchange(new Message { Body = "string body" });
        await processor.Process(exchange);

        exchange.In.Headers[ClaimCheckHeaders.OriginalBodyType].Should().Be(typeof(string).FullName);
    }

    [Fact]
    public async Task Processor_GetAndRemove_RestoresBody()
    {
        var repo = new InMemoryClaimCheckRepository();

        // Store
        var setProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.Set, "k1");
        var exchange = new Exchange(new Message { Body = "original data" });
        await setProc.Process(exchange);

        // Retrieve
        var getProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.GetAndRemove, "k1");
        await getProc.Process(exchange);

        exchange.In.Body.Should().Be("original data");
        exchange.In.Headers.Should().NotContainKey(ClaimCheckHeaders.Key);
        repo.Count.Should().Be(0);
    }

    [Fact]
    public async Task Processor_Get_KeepsDataInStore()
    {
        var repo = new InMemoryClaimCheckRepository();

        var setProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.Set, "k1");
        var exchange = new Exchange(new Message { Body = "keep me" });
        await setProc.Process(exchange);

        var getProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.Get, "k1");
        await getProc.Process(exchange);

        exchange.In.Body.Should().Be("keep me");
        repo.Count.Should().Be(1); // still in store
    }

    [Fact]
    public async Task Processor_Get_MissingKey_LeavesBodyUnchanged()
    {
        var repo = new InMemoryClaimCheckRepository();
        var processor = new ClaimCheckProcessor(repo, ClaimCheckOperation.Get, "nonexistent");

        var exchange = new Exchange(new Message { Body = "untouched" });
        await processor.Process(exchange);

        exchange.In.Body.Should().Be("untouched");
    }

    // ══════════════════════════════════════════════════════════════
    // ClaimCheckProcessor — Push / Pop (stack semantics)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_PushPop_Roundtrip()
    {
        var repo = new InMemoryClaimCheckRepository();

        // Push
        var pushProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.Push);
        var exchange = new Exchange(new Message { Body = "stashed body" });
        await pushProc.Process(exchange);

        exchange.In.Body.Should().NotBe("stashed body"); // body replaced with key

        // Modify body in between
        exchange.In.Body = "temporary data";

        // Pop
        var popProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.Pop);
        await popProc.Process(exchange);

        exchange.In.Body.Should().Be("stashed body");
    }

    [Fact]
    public async Task Processor_PushPush_PopPop_LIFO()
    {
        var repo = new InMemoryClaimCheckRepository();
        var push = new ClaimCheckProcessor(repo, ClaimCheckOperation.Push);
        var pop = new ClaimCheckProcessor(repo, ClaimCheckOperation.Pop);

        var exchange = new Exchange(new Message { Body = "first" });
        await push.Process(exchange);

        exchange.In.Body = "second";
        await push.Process(exchange);

        // Pop should return "second" first (LIFO)
        await pop.Process(exchange);
        exchange.In.Body.Should().Be("second");

        await pop.Process(exchange);
        exchange.In.Body.Should().Be("first");
    }

    [Fact]
    public async Task Processor_Pop_EmptyStack_LeavesBodyUnchanged()
    {
        var repo = new InMemoryClaimCheckRepository();
        var popProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.Pop);

        var exchange = new Exchange(new Message { Body = "original" });
        await popProc.Process(exchange);

        exchange.In.Body.Should().Be("original");
    }

    // ══════════════════════════════════════════════════════════════
    // ClaimCheckProcessor — byte[] body roundtrip
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_ByteArray_Roundtrip()
    {
        var repo = new InMemoryClaimCheckRepository();
        var originalBytes = new byte[] { 0x00, 0xFF, 0x42, 0x13 };

        var setProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.Set, "bin");
        var exchange = new Exchange(new Message { Body = originalBytes });
        await setProc.Process(exchange);

        var getProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.GetAndRemove, "bin");
        await getProc.Process(exchange);

        exchange.In.Body.Should().BeOfType<byte[]>();
        ((byte[])exchange.In.Body!).Should().BeEquivalentTo(originalBytes);
    }

    // ══════════════════════════════════════════════════════════════
    // ClaimCheckProcessor — auto-key (no explicit key, uses header)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_AutoKey_SetAndGetViaHeader()
    {
        var repo = new InMemoryClaimCheckRepository();

        // Set without explicit key -> auto-generates key, stores in header
        var setProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.Set);
        var exchange = new Exchange(new Message { Body = "auto keyed data" });
        await setProc.Process(exchange);

        var autoKey = exchange.In.Headers[ClaimCheckHeaders.Key] as string;
        autoKey.Should().NotBeNullOrEmpty();

        // Get without explicit key -> reads key from header
        var getProc = new ClaimCheckProcessor(repo, ClaimCheckOperation.GetAndRemove);
        await getProc.Process(exchange);

        exchange.In.Body.Should().Be("auto keyed data");
    }

    // ══════════════════════════════════════════════════════════════
    // ClaimCheckSerializer
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Serializer_Null_ReturnsEmpty()
    {
        var result = ClaimCheckSerializer.Serialize(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Serializer_ByteArray_ReturnsSameBytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var result = ClaimCheckSerializer.Serialize(bytes);
        result.Should().BeSameAs(bytes);
    }

    [Fact]
    public void Serializer_String_ReturnsUtf8()
    {
        var result = ClaimCheckSerializer.Serialize("hello");
        result.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("hello"));
    }

    [Fact]
    public void Deserializer_Empty_ReturnsNull()
    {
        var result = ClaimCheckSerializer.Deserialize([], null);
        result.Should().BeNull();
    }

    [Fact]
    public void Deserializer_ByteArrayType_ReturnsBytes()
    {
        var data = new byte[] { 1, 2, 3 };
        var result = ClaimCheckSerializer.Deserialize(data, typeof(byte[]).FullName);
        result.Should().BeOfType<byte[]>();
        ((byte[])result!).Should().BeEquivalentTo(data);
    }

    [Fact]
    public void Deserializer_Default_ReturnsString()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        var result = ClaimCheckSerializer.Deserialize(data, null);
        result.Should().Be("hello world");
    }
}
