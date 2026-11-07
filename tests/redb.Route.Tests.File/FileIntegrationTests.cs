using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// End-to-end integration tests: FileConsumer → IProcessor → FileProducer.
/// Simulates a file-copy route using real temp directories on disk.
/// </summary>
public class FileIntegrationTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;
    private FileConsumer? _consumer;
    private FileProducer? _producer;

    // Captured exchanges
    private readonly List<IExchange> _capturedExchanges = [];

    public FileIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "redb-route-file-integ-" + Guid.NewGuid().ToString("N")[..8]);
        _inputDir = Path.Combine(_tempDir, "input");
        _outputDir = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_consumer is not null) await _consumer.Stop();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (FileConsumer consumer, FileProducer producer) CreatePair(
        Dictionary<string, string>? consumerParams = null,
        Dictionary<string, string>? producerParams = null)
    {
        var component = new FileComponent();

        // Consumer endpoint
        var cPath = "/" + _inputDir.Replace("\\", "/");
        var cParms = consumerParams ?? new Dictionary<string, string>();
        if (!cParms.ContainsKey("delay")) cParms["delay"] = "100";
        if (!cParms.ContainsKey("delete")) cParms["delete"] = "true";
        var cUri = new EndpointUri("file", cPath, $"file://{cPath}", cParms);
        var cEndpoint = (FileEndpoint)component.CreateEndpoint(cUri);

        // Producer endpoint
        var pPath = "/" + _outputDir.Replace("\\", "/");
        var pParms = producerParams ?? new Dictionary<string, string>();
        var pUri = new EndpointUri("file", pPath, $"file://{pPath}", pParms);
        var pEndpoint = (FileEndpoint)component.CreateEndpoint(pUri);
        _producer = (FileProducer)pEndpoint.CreateProducer();

        // Processor bridges consumer→producer
        var producer = _producer;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                lock (_capturedExchanges) _capturedExchanges.Add(ex);
                await producer.Process(ex);
            });

        _consumer = (FileConsumer)cEndpoint.CreateConsumer(processor);
        return (_consumer, _producer);
    }

    private void WriteInputFile(string name, string content = "test data")
    {
        System.IO.File.WriteAllText(Path.Combine(_inputDir, name), content);
    }

    private string ReadOutputFile(string name)
    {
        return System.IO.File.ReadAllText(Path.Combine(_outputDir, name));
    }

    private bool OutputFileExists(string name)
    {
        return System.IO.File.Exists(Path.Combine(_outputDir, name));
    }

    // ── End-to-end copy ─────────────────────────────────────────────

    [Fact]
    public async Task EndToEnd_SingleFile_CopiedToOutput()
    {
        WriteInputFile("order.csv", "col1,col2\nA,B");
        var (consumer, _) = CreatePair();
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("order.csv"), 5000);
        await consumer.Stop();

        ReadOutputFile("order.csv").Should().Be("col1,col2\nA,B");
        // Source removed (delete=true)
        System.IO.File.Exists(Path.Combine(_inputDir, "order.csv")).Should().BeFalse();
    }

    [Fact]
    public async Task EndToEnd_MultipleFiles_AllCopied()
    {
        for (var i = 0; i < 5; i++)
            WriteInputFile($"file{i}.txt", $"content-{i}");

        var (consumer, _) = CreatePair();
        await consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 5;
        }, 5000);
        await consumer.Stop();

        for (var i = 0; i < 5; i++)
        {
            OutputFileExists($"file{i}.txt").Should().BeTrue($"file{i}.txt should exist in output");
            ReadOutputFile($"file{i}.txt").Should().Be($"content-{i}");
        }
    }

    [Fact]
    public async Task EndToEnd_BinaryFile_ContentPreserved()
    {
        var binaryData = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x80, 0x7F };
        System.IO.File.WriteAllBytes(Path.Combine(_inputDir, "data.bin"), binaryData);

        var (consumer, _) = CreatePair();
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("data.bin"), 5000);
        await consumer.Stop();

        var output = System.IO.File.ReadAllBytes(Path.Combine(_outputDir, "data.bin"));
        output.Should().BeEquivalentTo(binaryData);
    }

    // ── Include filter ──────────────────────────────────────────────

    [Fact]
    public async Task EndToEnd_IncludeFilter_OnlyMatchingFilesCopied()
    {
        WriteInputFile("report.csv", "csv data");
        WriteInputFile("notes.txt", "text data");
        WriteInputFile("summary.csv", "more csv");

        var (consumer, _) = CreatePair(new Dictionary<string, string> { ["include"] = "*.csv" });
        await consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 2;
        }, 5000);
        await consumer.Stop();

        OutputFileExists("report.csv").Should().BeTrue();
        OutputFileExists("summary.csv").Should().BeTrue();
        OutputFileExists("notes.txt").Should().BeFalse("txt file should not be copied");
    }

    // ── Noop mode ───────────────────────────────────────────────────

    [Fact]
    public async Task EndToEnd_Noop_SourceFileNotDeleted()
    {
        WriteInputFile("keep.txt", "keep me");

        var (consumer, _) = CreatePair(new Dictionary<string, string>
        {
            ["noop"] = "true",
            ["delete"] = "false",
            ["idempotent"] = "true"   // prevent re-processing
        });
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("keep.txt"), 5000);
        await consumer.Stop();

        System.IO.File.Exists(Path.Combine(_inputDir, "keep.txt")).Should().BeTrue("noop should keep source");
        ReadOutputFile("keep.txt").Should().Be("keep me");
    }

    // ── MoveTo ──────────────────────────────────────────────────────

    [Fact]
    public async Task EndToEnd_MoveTo_SourceMovedToArchive()
    {
        var archiveDir = Path.Combine(_tempDir, "archive");
        WriteInputFile("invoice.pdf", "pdf bytes");

        var (consumer, _) = CreatePair(new Dictionary<string, string>
        {
            ["delete"] = "false",
            ["moveTo"] = archiveDir
        });
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("invoice.pdf"), 5000);
        await consumer.Stop();

        // Source moved to archive
        System.IO.File.Exists(Path.Combine(_inputDir, "invoice.pdf")).Should().BeFalse();
        System.IO.File.Exists(Path.Combine(archiveDir, "invoice.pdf")).Should().BeTrue();
        // Output also written
        ReadOutputFile("invoice.pdf").Should().Be("pdf bytes");
    }

    // ── Headers propagation through the route ───────────────────────

    [Fact]
    public async Task EndToEnd_ConsumerHeaders_PropagatedThroughRoute()
    {
        WriteInputFile("report.csv", "col1\nval1");

        var (consumer, _) = CreatePair();
        await consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 1;
        }, 5000);
        await consumer.Stop();

        IExchange captured;
        lock (_capturedExchanges) captured = _capturedExchanges[0];

        captured.In.Headers.Should().ContainKey(FileHeaders.FileName);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileNameOnly);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileExtension);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileAbsolutePath);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileLength);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileLastModified);

        captured.In.Headers[FileHeaders.FileName].Should().Be("report.csv");
        captured.In.Headers[FileHeaders.FileNameOnly].Should().Be("report");
        captured.In.Headers[FileHeaders.FileExtension].Should().Be(".csv");
        ((long)captured.In.Headers[FileHeaders.FileLength]!).Should().BeGreaterThan(0);
    }

    // ── Producer FileExist: Append ──────────────────────────────────

    [Fact]
    public async Task EndToEnd_ProducerAppend_AccumulatesContent()
    {
        // Pre-create an existing output file
        System.IO.File.WriteAllText(Path.Combine(_outputDir, "log.txt"), "line1\n");
        WriteInputFile("log.txt", "line2\n");

        var (consumer, _) = CreatePair(
            producerParams: new Dictionary<string, string> { ["fileExist"] = "Append" });
        await consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 1;
        }, 5000);
        await consumer.Stop();

        var content = ReadOutputFile("log.txt");
        content.Should().Contain("line1");
        content.Should().Contain("line2");
    }

    // ── Producer with TempPrefix (atomic writes) ────────────────────

    [Fact]
    public async Task EndToEnd_TempPrefix_AtomicWrite()
    {
        WriteInputFile("atomic.dat", "important data");

        var (consumer, _) = CreatePair(
            producerParams: new Dictionary<string, string> { ["tempPrefix"] = ".tmp_" });
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("atomic.dat"), 5000);
        await consumer.Stop();

        ReadOutputFile("atomic.dat").Should().Be("important data");
        // No temp files should remain
        Directory.GetFiles(_outputDir, ".tmp_*").Should().BeEmpty();
    }

    // ── SortBy with multiple files ──────────────────────────────────

    [Fact]
    public async Task EndToEnd_SortByName_ProcessedInAlphaOrder()
    {
        WriteInputFile("c_third.txt", "C");
        WriteInputFile("a_first.txt", "A");
        WriteInputFile("b_second.txt", "B");

        var (consumer, _) = CreatePair(new Dictionary<string, string> { ["sortBy"] = "name" });
        await consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 3;
        }, 5000);
        await consumer.Stop();

        List<IExchange> snapshot;
        lock (_capturedExchanges) snapshot = [.. _capturedExchanges];

        snapshot.Should().HaveCountGreaterThanOrEqualTo(3);
        var names = snapshot.Select(e => (string)e.In.Headers[FileHeaders.FileName]!).ToList();
        names.Should().ContainInOrder("a_first.txt", "b_second.txt", "c_third.txt");
    }

    // ── Large file ──────────────────────────────────────────────────

    [Fact]
    public async Task EndToEnd_LargeFile_PreservedExactly()
    {
        var largeContent = new string('X', 1_000_000); // 1MB text
        WriteInputFile("large.bin", largeContent);

        var (consumer, _) = CreatePair();
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("large.bin"), 10000);
        await consumer.Stop();

        ReadOutputFile("large.bin").Should().Be(largeContent);
    }

    // ── Files arriving after consumer started ───────────────────────

    [Fact]
    public async Task EndToEnd_FileArrivesWhileRunning_StillProcessed()
    {
        var (consumer, _) = CreatePair();
        await consumer.Start();

        // Wait one poll cycle
        await Task.Delay(200);

        // File drops in while consumer is running
        WriteInputFile("late.txt", "arrived late");

        await WaitForCondition(() => OutputFileExists("late.txt"), 5000);
        await consumer.Stop();

        ReadOutputFile("late.txt").Should().Be("arrived late");
    }

    // ── ProcessedCount / WriteCount counters ────────────────────────

    [Fact]
    public async Task EndToEnd_Counters_IncrementCorrectly()
    {
        for (var i = 0; i < 3; i++)
            WriteInputFile($"count{i}.txt", $"data{i}");

        var (consumer, producer) = CreatePair();
        await consumer.Start();

        await WaitForCondition(() => consumer.ProcessedCount >= 3, 5000);
        await consumer.Stop();

        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(3);
    }

    // ── StreamBody end-to-end ───────────────────────────────────────

    [Fact]
    public async Task EndToEnd_StreamBody_SingleFile_CopiedToOutput()
    {
        WriteInputFile("stream-order.csv", "col1,col2\nX,Y");
        var (consumer, _) = CreatePair(
            consumerParams: new() { ["streamBody"] = "true" });
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("stream-order.csv"), 5000);
        await consumer.Stop();

        ReadOutputFile("stream-order.csv").Should().Be("col1,col2\nX,Y");
        System.IO.File.Exists(Path.Combine(_inputDir, "stream-order.csv")).Should().BeFalse();
    }

    [Fact]
    public async Task EndToEnd_StreamBody_BinaryPreserved()
    {
        var binaryData = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x80, 0x7F, 0xAB, 0xCD };
        System.IO.File.WriteAllBytes(Path.Combine(_inputDir, "binary.dat"), binaryData);

        var (consumer, _) = CreatePair(
            consumerParams: new() { ["streamBody"] = "true" });
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("binary.dat"), 5000);
        await consumer.Stop();

        System.IO.File.ReadAllBytes(Path.Combine(_outputDir, "binary.dat"))
            .Should().BeEquivalentTo(binaryData);
    }

    [Fact]
    public async Task EndToEnd_StreamBody_LargeFile_NoOOM()
    {
        // 2MB file — would OOM if we accidentally doubled buffers
        var largeContent = new string('Z', 2_000_000);
        WriteInputFile("large-stream.bin", largeContent);

        var (consumer, _) = CreatePair(
            consumerParams: new() { ["streamBody"] = "true" });
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("large-stream.bin"), 10000);
        await consumer.Stop();

        ReadOutputFile("large-stream.bin").Should().Be(largeContent);
    }

    [Fact]
    public async Task EndToEnd_StreamBody_DeleteAfterRead_FileRemoved()
    {
        WriteInputFile("delete-me.txt", "bye");
        var filePath = Path.Combine(_inputDir, "delete-me.txt");

        var (consumer, _) = CreatePair(
            consumerParams: new() { ["streamBody"] = "true", ["delete"] = "true" });
        await consumer.Start();

        await WaitForCondition(() => !System.IO.File.Exists(filePath), 5000);
        await consumer.Stop();

        System.IO.File.Exists(filePath).Should().BeFalse();
        OutputFileExists("delete-me.txt").Should().BeTrue();
        ReadOutputFile("delete-me.txt").Should().Be("bye");
    }

    [Fact]
    public async Task EndToEnd_StreamBody_MoveTo_SourceMovedToArchive()
    {
        var archiveDir = Path.Combine(_tempDir, "archive-stream");
        WriteInputFile("move-stream.txt", "move me");

        var (consumer, _) = CreatePair(
            consumerParams: new()
            {
                ["streamBody"] = "true", ["delete"] = "false", ["moveTo"] = archiveDir
            });
        await consumer.Start();

        await WaitForCondition(() => OutputFileExists("move-stream.txt"), 5000);
        await consumer.Stop();

        System.IO.File.Exists(Path.Combine(_inputDir, "move-stream.txt")).Should().BeFalse();
        System.IO.File.Exists(Path.Combine(archiveDir, "move-stream.txt")).Should().BeTrue();
        ReadOutputFile("move-stream.txt").Should().Be("move me");
    }

    [Fact]
    public async Task EndToEnd_StreamBody_MultipleFiles_AllCopied()
    {
        for (var i = 0; i < 5; i++)
            WriteInputFile($"stream-f{i}.txt", $"stream-content-{i}");

        var (consumer, _) = CreatePair(
            consumerParams: new() { ["streamBody"] = "true" });
        await consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 5;
        }, 5000);
        await consumer.Stop();

        for (var i = 0; i < 5; i++)
        {
            OutputFileExists($"stream-f{i}.txt").Should().BeTrue();
            ReadOutputFile($"stream-f{i}.txt").Should().Be($"stream-content-{i}");
        }
    }

    [Fact]
    public async Task EndToEnd_StreamBody_BodyIsStreamDuringProcessing()
    {
        WriteInputFile("check-type.txt", "type-check");
        Type? bodyType = null;

        var component = new FileComponent();
        var cPath = "/" + _inputDir.Replace("\\", "/");
        var cUri = new EndpointUri("file", cPath, $"file://{cPath}",
            new Dictionary<string, string> { ["streamBody"] = "true", ["delay"] = "100", ["noop"] = "true", ["idempotent"] = "true" });
        var cEndpoint = (FileEndpoint)component.CreateEndpoint(cUri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => bodyType = x.Arg<IExchange>().In.Body?.GetType());

        _consumer = (FileConsumer)cEndpoint.CreateConsumer(processor);
        await _consumer.Start();

        await WaitForCondition(() => bodyType != null, 3000);
        await _consumer.Stop();

        bodyType.Should().BeAssignableTo<Stream>();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task WaitForCondition(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(50);
        }
    }
}
