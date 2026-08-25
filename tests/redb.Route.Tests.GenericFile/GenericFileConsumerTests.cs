using redb.Route.Abstractions;

namespace redb.Route.Tests.GenericFile;

/// <summary>
/// Behaviour tests for the shared poll loop in <c>GenericFileConsumer</c>.
/// These run against <see cref="FakeFileOperations"/>, so they cover the code path
/// used by the File, FTP and SFTP transports without touching a disk or a server.
/// </summary>
public class GenericFileConsumerTests : GenericFileTestBase
{
    // ── Baseline: the pipeline works ────────────────────────────────

    [Fact]
    public async Task Poll_ReadsBody_SetsHeaders_AndDeletes()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");

        var endpoint = Endpoint(new() { ["delete"] = "true" });
        var (processor, bodies, exchanges) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        bodies.Should().ContainSingle().Which.Should().Be("PAYLOAD");
        exchanges[0].In.Headers["testFile.Name"].Should().Be("order.csv");
        Ops.HasFile("/in/order.csv").Should().BeFalse();
    }

    [Fact]
    public async Task Poll_AppliesIncludeExcludeSortAndLimit()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        Ops.AddFile("/in/c.csv", "3", t0.AddMinutes(3));
        Ops.AddFile("/in/a.csv", "1", t0.AddMinutes(1));
        Ops.AddFile("/in/b.csv", "2", t0.AddMinutes(2));
        Ops.AddFile("/in/skip.tmp", "x", t0);

        var endpoint = Endpoint(new()
        {
            ["include"] = "*.csv",
            ["exclude"] = "*.tmp",
            ["sortBy"] = "Modified",
            ["maxMessagesPerPoll"] = "2",
            ["noop"] = "true"
        });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        bodies.Should().Equal("1", "2");
    }

    [Fact]
    public async Task Poll_SkipsInternalTempFiles()
    {
        Ops.AddFile("/in/.redb_staging.csv", "internal");
        Ops.AddFile("/in/.tmp-partial.csv", "partial");
        Ops.AddFile("/in/real.csv", "real");

        var endpoint = Endpoint(new() { ["tempPrefix"] = ".tmp-", ["noop"] = "true" });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        bodies.Should().ContainSingle().Which.Should().Be("real");
    }

    // ── Failure contract ────────────────────────────────────────────

    [Fact]
    public async Task ProcessingFailure_KeepsFile_AndReleasesIdempotentKey()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");

        var endpoint = Endpoint(new() { ["delete"] = "true", ["idempotent"] = "true" });
        var (processor, _, _) = Collector(exchange => exchange.Exception = new FormatException("bad row"));
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        Ops.HasFile("/in/order.csv").Should().BeTrue("a file that failed processing must not be deleted");

        // Second poll must retry it: the idempotent key was released on failure.
        var (retryProcessor, retryBodies, _) = Collector();
        var retryConsumer = (TestFileConsumer)endpoint.CreateConsumer(retryProcessor);
        await retryConsumer.PollOnceAsync();
        retryBodies.Should().ContainSingle();
    }

    [Fact]
    public async Task UnreadableFile_IsNotDeliveredAsEmptyBody()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");
        Ops.FailRead = path => path == "/in/order.csv"
            ? new IOException("The process cannot access the file because it is being used by another process.")
            : null;

        var endpoint = Endpoint(new() { ["delete"] = "true" });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        bodies.Should().NotContain("", "an unreadable file must never arrive as a successfully processed empty body");
        Ops.HasFile("/in/order.csv").Should().BeTrue("an unreadable file must not be treated as processed");
    }

    [Fact]
    public async Task UnreadableFile_DoesNotAbortTheRestOfTheBatch()
    {
        Ops.AddFile("/in/a.csv", "A");
        Ops.AddFile("/in/b.csv", "B");
        Ops.FailRead = path => path == "/in/a.csv" ? new IOException("locked") : null;

        var endpoint = Endpoint(new() { ["sortBy"] = "Name", ["noop"] = "true" });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        bodies.Should().Contain("B", "one unreadable file must not block the files after it");
    }

    // ── Idempotency ─────────────────────────────────────────────────

    [Fact]
    public async Task Idempotent_DefaultKey_SkipsAlreadyProcessedFile()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");

        var endpoint = Endpoint(new() { ["idempotent"] = "true", ["noop"] = "true" });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();
        await consumer.PollOnceAsync();

        bodies.Should().ContainSingle();
    }

    [Fact]
    public async Task Idempotent_LostReadLock_ProcessesFileOnceLockIsAvailable()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");

        var endpoint = Endpoint(new() { ["idempotent"] = "true", ["noop"] = "true" });
        var locked = true;
        endpoint.ReadLockGate = _ => !locked;

        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();       // another consumer holds the lock
        bodies.Should().BeEmpty();

        locked = false;                        // the other consumer is done
        await consumer.PollOnceAsync();

        bodies.Should().ContainSingle("claiming the idempotent key must not consume the file when the read lock was refused");
    }

    [Fact]
    public async Task Idempotent_TreatsNamesDifferingOnlyInCaseAsDistinctFiles()
    {
        // Every SFTP/FTP server and every non-Windows file system treats these as two files.
        // Same timestamp and same length on purpose: the path case must be what separates the keys.
        var stamp = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        Ops.AddFile("/in/order.csv", "lower", stamp);
        Ops.AddFile("/in/Order.csv", "upper", stamp);

        var endpoint = Endpoint(new() { ["idempotent"] = "true", ["noop"] = "true" });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        bodies.Should().HaveCount(2, "case-insensitive keys collapse two real files into one");
        bodies.Should().BeEquivalentTo(["lower", "upper"]);
    }

    [Fact]
    public async Task Idempotent_CustomKey_ProcessesDistinctFiles()
    {
        Ops.AddFile("/in/one.csv", "1");
        Ops.AddFile("/in/two.csv", "2");

        var endpoint = Endpoint(new()
        {
            ["idempotent"] = "true",
            ["idempotentKey"] = "${file:name}",
            ["noop"] = "true"
        });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        bodies.Should().HaveCount(2, "a per-file key expression must produce a distinct key per file");
        bodies.Should().BeEquivalentTo(["1", "2"]);
    }

    [Fact]
    public async Task Idempotent_CustomKey_StillSkipsRepeats()
    {
        Ops.AddFile("/in/one.csv", "1");

        var endpoint = Endpoint(new()
        {
            ["idempotent"] = "true",
            ["idempotentKey"] = "${file:name}",
            ["noop"] = "true"
        });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();
        await consumer.PollOnceAsync();

        bodies.Should().ContainSingle();
    }

    // ── Post-processing ─────────────────────────────────────────────

    [Fact]
    public async Task MoveTo_MovesFileToDirectory()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");

        var endpoint = Endpoint(new() { ["moveTo"] = "archive" });
        var (processor, _, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        Ops.HasFile("/in/archive/order.csv").Should().BeTrue();
        Ops.HasFile("/in/order.csv").Should().BeFalse();
    }

    [Fact]
    public async Task MoveTo_ResolvesFileNameSubstitutions()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");

        var endpoint = Endpoint(new() { ["moveTo"] = "archive-${file:name.noext}" });
        var (processor, _, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        Ops.HasFile("/in/archive-order/order.csv").Should()
            .BeTrue("moveTo must resolve the same file substitutions as doneFileName");
        Ops.AllDirectories().Should().NotContain(d => d.Contains("${"),
            "an unresolved template must never become a directory name");
    }

    [Fact]
    public async Task PreMove_MovesBeforeProcessing_AndResolvesSubstitutions()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");

        var endpoint = Endpoint(new() { ["preMove"] = "work-${file:name.noext}", ["delete"] = "true" });
        var (processor, _, exchanges) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        exchanges[0].In.Headers["testFile.AbsolutePath"].Should().Be("/in/work-order/order.csv");
        Ops.AllDirectories().Should().NotContain(d => d.Contains("${"));
    }

    [Fact]
    public async Task DoneFile_GatesProcessing_AndIsRemovedAfterwards()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");

        var endpoint = Endpoint(new() { ["doneFileName"] = "${file:name}.done", ["noop"] = "true" });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();
        bodies.Should().BeEmpty("no done file yet");

        Ops.AddFile("/in/order.csv.done", "");
        await consumer.PollOnceAsync();

        bodies.Should().ContainSingle();
        Ops.HasFile("/in/order.csv.done").Should().BeFalse("the done file is consumed with the payload");
    }

    [Fact]
    public async Task MinAge_DefersYoungFiles()
    {
        Ops.AddFile("/in/fresh.csv", "new", DateTimeOffset.UtcNow);
        Ops.AddFile("/in/settled.csv", "old", DateTimeOffset.UtcNow.AddMinutes(-5));

        var endpoint = Endpoint(new() { ["minAge"] = "60000", ["noop"] = "true" });
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)endpoint.CreateConsumer(processor);

        await consumer.PollOnceAsync();

        bodies.Should().ContainSingle().Which.Should().Be("old");
    }
}
