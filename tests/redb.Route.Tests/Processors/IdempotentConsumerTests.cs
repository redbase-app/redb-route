using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Tests for IdempotentConsumerProcessor and InMemoryIdempotentRepository.
/// </summary>
public class IdempotentConsumerTests
{
    // ── InMemoryIdempotentRepository ──

    [Fact]
    public async Task Repository_Add_ReturnsTrue_ForNewKey()
    {
        var repo = new InMemoryIdempotentRepository();
        var result = await repo.Add("key-1");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Repository_Add_ReturnsFalse_ForDuplicateKey()
    {
        var repo = new InMemoryIdempotentRepository();
        await repo.Add("key-1");
        var result = await repo.Add("key-1");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Repository_Contains_ReturnsTrueAfterAdd()
    {
        var repo = new InMemoryIdempotentRepository();
        await repo.Add("key-1");
        (await repo.Contains("key-1")).Should().BeTrue();
    }

    [Fact]
    public async Task Repository_Contains_ReturnsFalseForMissing()
    {
        var repo = new InMemoryIdempotentRepository();
        (await repo.Contains("missing")).Should().BeFalse();
    }

    [Fact]
    public async Task Repository_Remove_AllowsReAdd()
    {
        var repo = new InMemoryIdempotentRepository();
        await repo.Add("key-1");
        await repo.Remove("key-1");
        var result = await repo.Add("key-1");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Repository_Clear_RemovesAllKeys()
    {
        var repo = new InMemoryIdempotentRepository();
        await repo.Add("a");
        await repo.Add("b");
        await repo.Add("c");
        repo.Count.Should().Be(3);

        await repo.Clear();
        repo.Count.Should().Be(0);
    }

    [Fact]
    public async Task Repository_Confirm_IsNoOp()
    {
        var repo = new InMemoryIdempotentRepository();
        await repo.Add("key-1");
        await repo.Confirm("key-1");
        (await repo.Contains("key-1")).Should().BeTrue();
    }

    [Fact]
    public async Task Repository_Add_ThrowsOnNull()
    {
        var repo = new InMemoryIdempotentRepository();
        var act = async () => await repo.Add(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── TTL Eviction ──

    [Fact]
    public async Task Repository_WithTtl_ContainsReturnsFalseAfterExpiry()
    {
        var repo = new InMemoryIdempotentRepository(ttl: TimeSpan.FromMilliseconds(50));
        await repo.Add("key-ttl");

        (await repo.Contains("key-ttl")).Should().BeTrue();

        await Task.Delay(80);

        (await repo.Contains("key-ttl")).Should().BeFalse();
    }

    [Fact]
    public async Task Repository_WithTtl_ExpiredKeyEvictedOnAdd()
    {
        var repo = new InMemoryIdempotentRepository(ttl: TimeSpan.FromMilliseconds(50));
        await repo.Add("old-key");
        repo.Count.Should().Be(1);

        await Task.Delay(80);

        await repo.Add("new-key");
        // old-key should have been evicted during the Add call
        (await repo.Contains("old-key")).Should().BeFalse();
        (await repo.Contains("new-key")).Should().BeTrue();
    }

    [Fact]
    public async Task Repository_WithTtl_NonExpiredKeyStillValid()
    {
        var repo = new InMemoryIdempotentRepository(ttl: TimeSpan.FromSeconds(10));
        await repo.Add("fresh-key");

        (await repo.Contains("fresh-key")).Should().BeTrue();
        var result = await repo.Add("fresh-key");
        result.Should().BeFalse(); // Still there, not expired
    }

    [Fact]
    public async Task Repository_WithTtl_ExpiredKeyAllowsReAdd()
    {
        var repo = new InMemoryIdempotentRepository(ttl: TimeSpan.FromMilliseconds(50));
        await repo.Add("re-key");

        await Task.Delay(80);

        var result = await repo.Add("re-key");
        result.Should().BeTrue(); // Expired, so re-add succeeds
    }

    [Fact]
    public async Task Repository_NullTtl_KeysKeptIndefinitely()
    {
        var repo = new InMemoryIdempotentRepository(); // ttl = null (default)
        await repo.Add("forever");
        (await repo.Contains("forever")).Should().BeTrue();
        repo.Count.Should().Be(1);
    }

    // ── IdempotentConsumerProcessor ──

    [Fact]
    public void Ctor_ThrowsOnNullInner()
    {
        var repo = new InMemoryIdempotentRepository();
        var act = () => new IdempotentConsumerProcessor(null!, repo, e => "key");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_ThrowsOnNullRepository()
    {
        var inner = Substitute.For<IProcessor>();
        var act = () => new IdempotentConsumerProcessor(inner, null!, e => "key");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_ThrowsOnNullKeyExtractor()
    {
        var inner = Substitute.For<IProcessor>();
        var repo = new InMemoryIdempotentRepository();
        var act = () => new IdempotentConsumerProcessor(inner, repo, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Process_NewMessage_PassesToInner()
    {
        var inner = Substitute.For<IProcessor>();
        var repo = new InMemoryIdempotentRepository();
        var processor = new IdempotentConsumerProcessor(
            inner, repo, e => e.In.Headers["MessageId"]?.ToString()!);

        var msg = new Message { Body = "payload" };
        msg.Headers["MessageId"] = "unique-1";
        var exchange = new Exchange(msg);

        await processor.Process(exchange);

        await inner.Received(1).Process(exchange, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_DuplicateMessage_SkipDuplicate_StopsExchange()
    {
        var inner = Substitute.For<IProcessor>();
        var repo = new InMemoryIdempotentRepository();
        var processor = new IdempotentConsumerProcessor(
            inner, repo, e => e.In.Headers["MessageId"]?.ToString()!, skipDuplicate: true);

        var msg1 = new Message { Body = "first" };
        msg1.Headers["MessageId"] = "dup-1";
        var exchange1 = new Exchange(msg1);
        await processor.Process(exchange1);

        var msg2 = new Message { Body = "second" };
        msg2.Headers["MessageId"] = "dup-1"; // same key
        var exchange2 = new Exchange(msg2);
        await processor.Process(exchange2);

        // Inner called only once (for the first message)
        await inner.Received(1).Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());

        // Second exchange should be stopped and flagged as duplicate
        exchange2.IsStopped.Should().BeTrue();
        exchange2.Properties[IdempotentConsumerProcessor.DuplicatePropertyKey].Should().Be(true);
    }

    [Fact]
    public async Task Process_DuplicateMessage_NoSkip_PropagatesWithFlag()
    {
        var inner = Substitute.For<IProcessor>();
        var repo = new InMemoryIdempotentRepository();
        var processor = new IdempotentConsumerProcessor(
            inner, repo, e => e.In.Headers["MessageId"]?.ToString()!, skipDuplicate: false);

        var msg1 = new Message { Body = "first" };
        msg1.Headers["MessageId"] = "dup-2";
        var exchange1 = new Exchange(msg1);
        await processor.Process(exchange1);

        var msg2 = new Message { Body = "duplicate" };
        msg2.Headers["MessageId"] = "dup-2"; // same key
        var exchange2 = new Exchange(msg2);
        await processor.Process(exchange2);

        // Inner called twice (both messages processed)
        await inner.Received(2).Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());

        // Second exchange should have the duplicate flag but NOT be stopped
        exchange2.IsStopped.Should().BeFalse();
        exchange2.Properties[IdempotentConsumerProcessor.DuplicatePropertyKey].Should().Be(true);
    }

    [Fact]
    public async Task Process_NullKey_ProcessesWithoutDedup()
    {
        var inner = Substitute.For<IProcessor>();
        var repo = new InMemoryIdempotentRepository();
        var processor = new IdempotentConsumerProcessor(
            inner, repo, _ => null!, skipDuplicate: true);

        var exchange = new Exchange(new Message { Body = "test" });
        await processor.Process(exchange);

        await inner.Received(1).Process(exchange, Arg.Any<CancellationToken>());
        repo.Count.Should().Be(0);
    }

    [Fact]
    public async Task Process_EmptyKey_ProcessesWithoutDedup()
    {
        var inner = Substitute.For<IProcessor>();
        var repo = new InMemoryIdempotentRepository();
        var processor = new IdempotentConsumerProcessor(
            inner, repo, _ => "", skipDuplicate: true);

        var exchange = new Exchange(new Message { Body = "test" });
        await processor.Process(exchange);

        await inner.Received(1).Process(exchange, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_OnFailure_RemovesKeyForRetry()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("fail")));

        var repo = new InMemoryIdempotentRepository();
        var processor = new IdempotentConsumerProcessor(
            inner, repo, e => "retry-key");

        var exchange = new Exchange(new Message { Body = "test" });

        var act = async () => await processor.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // The key should have been removed to allow retry
        (await repo.Contains("retry-key")).Should().BeFalse();
    }

    [Fact]
    public async Task Process_OnSuccess_ConfirmsKey()
    {
        var inner = Substitute.For<IProcessor>();
        var repo = new InMemoryIdempotentRepository();
        var processor = new IdempotentConsumerProcessor(
            inner, repo, e => "confirm-key");

        var exchange = new Exchange(new Message { Body = "test" });
        await processor.Process(exchange);

        // Key should remain (confirmed)
        (await repo.Contains("confirm-key")).Should().BeTrue();
    }

    [Fact]
    public async Task Process_MultipleDistinctKeys_AllProcessed()
    {
        var processedBodies = new List<object?>();
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                processedBodies.Add(ci.Arg<IExchange>().In.Body);
                return Task.CompletedTask;
            });

        var repo = new InMemoryIdempotentRepository();
        var processor = new IdempotentConsumerProcessor(
            inner, repo, e => e.In.Body?.ToString()!);

        await processor.Process(new Exchange(new Message { Body = "a" }));
        await processor.Process(new Exchange(new Message { Body = "b" }));
        await processor.Process(new Exchange(new Message { Body = "c" }));

        processedBodies.Should().HaveCount(3);
        processedBodies.Should().ContainInOrder("a", "b", "c");
    }

    [Fact]
    public void DuplicatePropertyKey_HasCorrectValue()
    {
        IdempotentConsumerProcessor.DuplicatePropertyKey.Should().Be("CamelDuplicateMessage");
    }
}
