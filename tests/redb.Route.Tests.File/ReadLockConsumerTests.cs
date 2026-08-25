using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// End-to-end tests for read-lock strategies driven through the real <see cref="FileConsumer"/>.
/// The strategies are also covered in isolation by <see cref="ReadLockTests"/>; these tests exist
/// because a strategy can be correct on its own and still break the consumer that uses it.
/// </summary>
public class ReadLockConsumerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;

    public ReadLockConsumerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "redb-route-readlock-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        _inputDir = Path.Combine(_tempDir, "input");
        Directory.CreateDirectory(_inputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private FileEndpoint CreateEndpoint(Dictionary<string, string> parameters)
    {
        var component = new FileComponent();
        var path = "/" + _inputDir.Replace("\\", "/");
        var uri = new EndpointUri("file", path, $"file://{path}", parameters);
        return (FileEndpoint)component.CreateEndpoint(uri);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_inputDir, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static async Task WaitForCondition(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !condition())
            await Task.Delay(25);
    }

    private static (IProcessor Processor, List<string> Bodies) Collector()
    {
        var bodies = new List<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call =>
            {
                var body = call.Arg<IExchange>().In.Body;
                bodies.Add(body switch
                {
                    byte[] b => System.Text.Encoding.UTF8.GetString(b),
                    Stream s => new StreamReader(s).ReadToEnd(),
                    null => "<null>",
                    var other => other.ToString() ?? "<null>"
                });
            });
        return (processor, bodies);
    }

    // ── Rename ──────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_DeliversFileContent_AndCompletesPostProcessing()
    {
        CreateFile("order.csv", "PAYLOAD");
        var endpoint = CreateEndpoint(new() { ["readLock"] = "Rename", ["delete"] = "true", ["delay"] = "100" });
        var (processor, bodies) = Collector();
        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await WaitForCondition(() => bodies.Count >= 1, 3000);
        await Task.Delay(400); // let further polls run — a broken lock re-delivers forever
        await consumer.Stop();

        bodies.Should().ContainSingle().Which.Should().Be("PAYLOAD");
        Directory.GetFiles(_inputDir).Should().BeEmpty("the file is consumed and deleted, not renamed back");
    }

    [Fact]
    public async Task Rename_MovesFileToArchiveUnderOriginalName()
    {
        CreateFile("order.csv", "PAYLOAD");
        var archive = Path.Combine(_tempDir, "archive");
        var endpoint = CreateEndpoint(new()
        {
            ["readLock"] = "Rename",
            ["moveTo"] = archive,
            ["delay"] = "100"
        });
        var (processor, bodies) = Collector();
        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await WaitForCondition(() => bodies.Count >= 1, 3000);
        await Task.Delay(300);
        await consumer.Stop();

        System.IO.File.Exists(Path.Combine(archive, "order.csv")).Should().BeTrue();
        System.IO.File.ReadAllText(Path.Combine(archive, "order.csv")).Should().Be("PAYLOAD");
    }

    // ── FileLock ────────────────────────────────────────────────────

    [Fact]
    public async Task FileLock_DeliversFileContent_AndCompletesPostProcessing()
    {
        CreateFile("order.csv", "PAYLOAD");
        var endpoint = CreateEndpoint(new() { ["readLock"] = "FileLock", ["delete"] = "true", ["delay"] = "100" });
        var (processor, bodies) = Collector();
        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await WaitForCondition(() => bodies.Count >= 1, 3000);
        await Task.Delay(400);
        await consumer.Stop();

        bodies.Should().ContainSingle().Which.Should().Be("PAYLOAD");
        Directory.GetFiles(_inputDir).Should().BeEmpty();
    }

    [Fact]
    public async Task FileLock_StreamBody_DeliversFileContent()
    {
        CreateFile("order.csv", "STREAMED");
        var endpoint = CreateEndpoint(new()
        {
            ["readLock"] = "FileLock",
            ["streamBody"] = "true",
            ["delete"] = "true",
            ["delay"] = "100"
        });
        var (processor, bodies) = Collector();
        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await WaitForCondition(() => bodies.Count >= 1, 3000);
        await Task.Delay(300);
        await consumer.Stop();

        bodies.Should().ContainSingle().Which.Should().Be("STREAMED");
    }

    [Fact]
    public async Task FileLock_KeepsOtherReadersOut_WhileHeld()
    {
        var path = CreateFile("order.csv", "PAYLOAD");
        var strategy = new FileLockReadLock();
        var options = new FileEndpointOptions();

        var acquired = await strategy.AcquireLock(new FileInfo(path), options, CancellationToken.None);
        acquired.Should().BeTrue();

        // Another process opening the file for reading must be refused: the point of this
        // strategy is exclusivity, and it must survive the fix that made our own read work.
        var openOther = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        openOther.Should().Throw<IOException>();

        strategy.ReleaseLock(new FileInfo(path), options);

        using var reopened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        reopened.Should().NotBeNull();
    }

    // ── MarkerFile + idempotency ────────────────────────────────────

    [Fact]
    public async Task MarkerFile_Contended_ProcessesFileOnceTheOtherConsumerReleases()
    {
        CreateFile("order.csv", "PAYLOAD");
        var marker = Path.Combine(_inputDir, "order.csv" + new FileEndpointOptions().ReadLockMarkerFileExtension);
        System.IO.File.WriteAllText(marker, "held by another consumer");

        var endpoint = CreateEndpoint(new()
        {
            ["readLock"] = "MarkerFile",
            ["idempotent"] = "true",
            ["noop"] = "true",
            ["delay"] = "100"
        });
        var (processor, bodies) = Collector();
        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await Task.Delay(400);
        bodies.Should().BeEmpty("the marker belongs to another consumer");

        System.IO.File.Delete(marker); // the other consumer finished

        await WaitForCondition(() => bodies.Count >= 1, 3000);
        await consumer.Stop();

        bodies.Should().ContainSingle("losing the lock race must not consume the idempotent key")
            .Which.Should().Be("PAYLOAD");
    }
}
