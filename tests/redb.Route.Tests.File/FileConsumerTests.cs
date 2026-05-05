using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// Tests for FileConsumer — polling, filtering, idempotency, post-processing.
/// Uses real temp directories on the filesystem.
/// </summary>
public class FileConsumerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;

    public FileConsumerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "redb-route-consumer-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _inputDir = Path.Combine(_tempDir, "input");
        Directory.CreateDirectory(_inputDir);
    }

    private FileEndpoint CreateEndpoint(Dictionary<string, string>? parameters = null)
    {
        var component = new FileComponent();
        var path = "/" + _inputDir.Replace("\\", "/");
        var parms = parameters ?? new Dictionary<string, string>();
        var uri = new EndpointUri("file", path, $"file://{path}", parms);
        return (FileEndpoint)component.CreateEndpoint(uri);
    }

    private void CreateFile(string name, string content = "test data")
    {
        System.IO.File.WriteAllText(Path.Combine(_inputDir, name), content);
    }

    // ── Basic polling ───────────────────────────────────────────────

    [Fact]
    public async Task Consumer_PollsAndProcessesFile()
    {
        CreateFile("order.csv", "data");
        var endpoint = CreateEndpoint(new() { ["delete"] = "true", ["delay"] = "100" });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();

        await WaitForCondition(() => processed.Count >= 1, 3000);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(1);
        var exchange = processed[0];
        exchange.Pattern.Should().Be(ExchangePattern.InOnly);
        exchange.In.Headers[FileHeaders.FileName].Should().Be("order.csv");
        exchange.In.Headers[FileHeaders.FileNameOnly].Should().Be("order");
        exchange.In.Headers[FileHeaders.FileExtension].Should().Be(".csv");
        exchange.In.Headers[FileHeaders.FileLength].Should().BeOfType<long>();
        exchange.In.Headers[FileHeaders.FileLastModified].Should().BeOfType<DateTimeOffset>();
        exchange.In.Body.Should().BeOfType<byte[]>();
        ((byte[])exchange.In.Body!).Length.Should().Be(4); // "data"
    }

    [Fact]
    public async Task Consumer_EmptyDirectory_NoProcessing()
    {
        var endpoint = CreateEndpoint(new() { ["delay"] = "100" });
        var processor = Substitute.For<IProcessor>();
        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await Task.Delay(300);
        await consumer.Stop();

        await processor.DidNotReceive().Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consumer_NonExistentDirectory_NoError()
    {
        var component = new FileComponent();
        var nonExistent = Path.Combine(_tempDir, "nonexistent");
        var path = "/" + nonExistent.Replace("\\", "/");
        var uri = new EndpointUri("file", path, $"file://{path}", new Dictionary<string, string> { ["delay"] = "100" });
        var endpoint = (FileEndpoint)component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(300);
        await consumer.Stop();

        // No exception thrown, no processing
        await processor.DidNotReceive().Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());
    }

    // ── Include/Exclude filters ─────────────────────────────────────

    [Fact]
    public async Task Consumer_IncludeFilter_OnlyMatchingFiles()
    {
        CreateFile("order.csv", "csv data");
        CreateFile("readme.txt", "txt data");
        CreateFile("data.csv", "more csv");

        var endpoint = CreateEndpoint(new() { ["include"] = "*.csv", ["noop"] = "true", ["delay"] = "100" });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 2, 3000);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(2);
        processed.Should().AllSatisfy(e =>
            e.In.Headers[FileHeaders.FileName].ToString()!.EndsWith(".csv").Should().BeTrue());
    }

    [Fact]
    public async Task Consumer_ExcludeFilter_SkipsMatchingFiles()
    {
        CreateFile("order.csv", "csv data");
        CreateFile("readme.tmp", "temp data");

        var endpoint = CreateEndpoint(new() { ["exclude"] = "*.tmp", ["noop"] = "true", ["delay"] = "100" });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1, 3000);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(1);
        processed.Should().AllSatisfy(e =>
            e.In.Headers[FileHeaders.FileName].ToString()!.EndsWith(".tmp").Should().BeFalse());
    }

    [Fact]
    public async Task Consumer_CommaIncludeFilter_MultiplePatterns()
    {
        CreateFile("order.csv", "csv");
        CreateFile("data.json", "json");
        CreateFile("readme.txt", "txt");

        var endpoint = CreateEndpoint(new() { ["include"] = "*.csv,*.json", ["noop"] = "true", ["delay"] = "100" });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 2, 3000);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(2);
        processed.Should().NotContain(e => e.In.Headers[FileHeaders.FileName].ToString()!.EndsWith(".txt"));
    }

    // ── Recursive ───────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_Recursive_FindsSubdirFiles()
    {
        CreateFile("root.txt", "root");
        var subDir = Path.Combine(_inputDir, "sub");
        Directory.CreateDirectory(subDir);
        System.IO.File.WriteAllText(Path.Combine(subDir, "child.txt"), "child");

        var endpoint = CreateEndpoint(new() { ["recursive"] = "true", ["noop"] = "true", ["delay"] = "100" });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 2, 3000);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(2);
        var names = processed.Select(e => e.In.Headers[FileHeaders.FileName].ToString()).ToList();
        names.Should().Contain("root.txt");
        names.Should().Contain("child.txt");
    }

    [Fact]
    public async Task Consumer_NotRecursive_SkipsSubdirFiles()
    {
        CreateFile("root.txt", "root");
        var subDir = Path.Combine(_inputDir, "sub");
        Directory.CreateDirectory(subDir);
        System.IO.File.WriteAllText(Path.Combine(subDir, "child.txt"), "child");

        var endpoint = CreateEndpoint(new() { ["recursive"] = "false", ["noop"] = "true", ["delay"] = "100" });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1, 3000);
        await Task.Delay(200); // Extra time to ensure no more files processed
        await consumer.Stop();

        var names = processed.Select(e => e.In.Headers[FileHeaders.FileName].ToString()).Distinct().ToList();
        names.Should().Contain("root.txt");
        names.Should().NotContain("child.txt");
    }

    // ── Sort ────────────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_SortByName_ProcessesInOrder()
    {
        CreateFile("c_file.txt", "c");
        await Task.Delay(10);
        CreateFile("a_file.txt", "a");
        await Task.Delay(10);
        CreateFile("b_file.txt", "b");

        var endpoint = CreateEndpoint(new()
        {
            ["sortBy"] = "Name",
            ["noop"] = "true",
            ["delay"] = "5000",   // Long delay so only one poll
            ["maxMessagesPerPoll"] = "10"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);

        // Trigger single poll directly
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 3, 3000);
        await consumer.Stop();

        var names = processed.Select(e => e.In.Headers[FileHeaders.FileName].ToString()).ToList();
        names.Should().StartWith("a_file.txt");
    }

    // ── MaxMessagesPerPoll ──────────────────────────────────────────

    [Fact]
    public async Task Consumer_MaxMessagesPerPoll_LimitsCount()
    {
        for (int i = 0; i < 5; i++)
            CreateFile($"file{i}.txt", $"data{i}");

        var endpoint = CreateEndpoint(new()
        {
            ["maxMessagesPerPoll"] = "2",
            ["noop"] = "true",
            ["delay"] = "5000" // Long delay - one poll
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 2, 3000);
        // Give a bit more time to ensure no extra files are processed in this poll
        await Task.Delay(100);
        await consumer.Stop();

        processed.Should().HaveCount(2);
    }

    // ── Post-processing: Delete ─────────────────────────────────────

    [Fact]
    public async Task Consumer_Delete_RemovesFileAfterProcessing()
    {
        CreateFile("deleteme.txt", "bye");
        var filePath = Path.Combine(_inputDir, "deleteme.txt");

        var endpoint = CreateEndpoint(new() { ["delete"] = "true", ["delay"] = "100" });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => !System.IO.File.Exists(filePath), 3000);
        await consumer.Stop();

        System.IO.File.Exists(filePath).Should().BeFalse();
    }

    // ── Post-processing: MoveTo ─────────────────────────────────────

    [Fact]
    public async Task Consumer_MoveTo_MovesFileAfterProcessing()
    {
        CreateFile("moveme.txt", "content");
        var archiveDir = Path.Combine(_tempDir, "archive");

        var endpoint = CreateEndpoint(new() { ["moveTo"] = archiveDir, ["delay"] = "100" });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => System.IO.File.Exists(Path.Combine(archiveDir, "moveme.txt")), 3000);
        await consumer.Stop();

        System.IO.File.Exists(Path.Combine(_inputDir, "moveme.txt")).Should().BeFalse();
        System.IO.File.Exists(Path.Combine(archiveDir, "moveme.txt")).Should().BeTrue();
        System.IO.File.ReadAllText(Path.Combine(archiveDir, "moveme.txt")).Should().Be("content");
    }

    // ── Post-processing: Noop ───────────────────────────────────────

    [Fact]
    public async Task Consumer_Noop_KeepsFile()
    {
        CreateFile("keepme.txt", "content");
        var filePath = Path.Combine(_inputDir, "keepme.txt");

        var endpoint = CreateEndpoint(new() { ["noop"] = "true", ["delay"] = "100" });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1, 3000);
        await consumer.Stop();

        System.IO.File.Exists(filePath).Should().BeTrue();
    }

    // ── Idempotency ─────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_Idempotent_SkipsDuplicates()
    {
        CreateFile("unique.txt", "data");

        var endpoint = CreateEndpoint(new() { ["idempotent"] = "true", ["noop"] = "true", ["delay"] = "100" });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1, 3000);
        // Wait for extra polls — file should not be processed again
        await Task.Delay(500);
        await consumer.Stop();

        processed.Should().HaveCount(1);
    }

    // ── PreMove ─────────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_PreMove_MovesBeforeProcessing()
    {
        CreateFile("premove.txt", "data");
        var preMoveDir = Path.Combine(_tempDir, "processing");

        var endpoint = CreateEndpoint(new() { ["preMove"] = preMoveDir, ["delete"] = "true", ["delay"] = "100" });
        string? processedPath = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processedPath = x.Arg<IExchange>().In.Headers[FileHeaders.FileAbsolutePath]?.ToString());

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processedPath != null, 3000);
        await consumer.Stop();

        // File should have been moved to preMove dir before processing
        processedPath.Should().Contain("processing");
    }

    // ── DoneFileName ────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_DoneFileName_WaitsForDoneFile()
    {
        CreateFile("data.csv", "test");

        var endpoint = CreateEndpoint(new()
        {
            ["doneFileName"] = "${file:name}.done",
            ["noop"] = "true",
            ["delay"] = "100"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();

        // Wait — file should NOT be processed (no .done file)
        await Task.Delay(400);
        processed.Should().BeEmpty();

        // Create the done file
        CreateFile("data.csv.done", "");

        await WaitForCondition(() => processed.Count >= 1, 3000);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    // ── Start/Stop lifecycle ────────────────────────────────────────

    [Fact]
    public async Task Consumer_StopIdempotent()
    {
        var endpoint = CreateEndpoint(new() { ["delay"] = "100" });
        var processor = Substitute.For<IProcessor>();
        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await consumer.Stop();
        await consumer.Stop(); // Should not throw
    }

    [Fact]
    public async Task Consumer_ProcessedCount_Increments()
    {
        CreateFile("file1.txt", "a");
        CreateFile("file2.txt", "b");

        var endpoint = CreateEndpoint(new() { ["delete"] = "true", ["delay"] = "100" });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => consumer.ProcessedCount >= 2, 3000);
        await consumer.Stop();

        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(2);
    }

    // ── GlobMatch tests ─────────────────────────────────────────────

    [Theory]
    [InlineData("order.csv", "*.csv", true)]
    [InlineData("order.CSV", "*.csv", true)]
    [InlineData("order.txt", "*.csv", false)]
    [InlineData("report.json", "*.json", true)]
    [InlineData("test.file", "test.*", true)]
    [InlineData("test.file", "test.?ile", true)]
    [InlineData("test.file", "t?st.file", true)]
    [InlineData("data.csv", "*.csv,*.json", true)]
    [InlineData("data.json", "*.csv,*.json", true)]
    [InlineData("data.txt", "*.csv,*.json", false)]
    public void GlobMatch_VariousPatterns(string input, string pattern, bool expected)
    {
        FileConsumer.GlobMatch(input, pattern).Should().Be(expected);
    }

    // ── MinAge ──────────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_MinAge_SkipsNewFiles()
    {
        var endpoint = CreateEndpoint(new()
        {
            ["minAge"] = "60000", // 60 seconds
            ["noop"] = "true",
            ["delay"] = "100"
        });
        var processor = Substitute.For<IProcessor>();

        CreateFile("newfile.txt", "data");

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(400);
        await consumer.Stop();

        // File is too new — should not be processed
        await processor.DidNotReceive().Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());
    }

    // ── Headers are populated correctly ─────────────────────────────

    [Fact]
    public async Task Consumer_PopulatesAllHeaders()
    {
        CreateFile("order.csv", "hello");

        var endpoint = CreateEndpoint(new() { ["noop"] = "true", ["delay"] = "100" });
        IExchange? captured = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => captured = x.Arg<IExchange>());

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => captured != null, 3000);
        await consumer.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey(FileHeaders.FileName);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileNameOnly);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileExtension);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileAbsolutePath);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileRelativePath);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileParent);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileLength);
        captured.In.Headers.Should().ContainKey(FileHeaders.FileLastModified);

        captured.In.Headers[FileHeaders.FileName].Should().Be("order.csv");
        captured.In.Headers[FileHeaders.FileNameOnly].Should().Be("order");
        captured.In.Headers[FileHeaders.FileExtension].Should().Be(".csv");
        ((long)captured.In.Headers[FileHeaders.FileLength]!).Should().Be(5);
    }

    // ── Excludes lock files ─────────────────────────────────────────

    [Fact]
    public async Task Consumer_ExcludesLockAndTempFiles()
    {
        CreateFile("data.csv", "data");
        CreateFile("data.csv.redbLock", "lock");
        CreateFile("data.csv.redbRename", "rename");
        CreateFile(".redb_temp", "temp");

        var endpoint = CreateEndpoint(new() { ["noop"] = "true", ["delay"] = "100" });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1, 3000);
        await Task.Delay(300);
        await consumer.Stop();

        var names = processed.Select(e => e.In.Headers[FileHeaders.FileName].ToString()).Distinct().ToList();
        names.Should().Contain("data.csv");
        names.Should().NotContain("data.csv.redbLock");
        names.Should().NotContain("data.csv.redbRename");
        names.Should().NotContain(".redb_temp");
    }

    // ── StreamBody ──────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_StreamBody_ReturnsStream()
    {
        CreateFile("report.csv", "stream-test-data");
        var endpoint = CreateEndpoint(new()
        {
            ["delete"] = "true", ["delay"] = "100", ["streamBody"] = "true"
        });
        bool? wasStream = null;
        bool? canRead = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                var body = x.Arg<IExchange>().In.Body;
                wasStream = body is Stream;
                if (body is Stream s) canRead = s.CanRead;
            });

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => wasStream.HasValue, 3000);
        await consumer.Stop();

        wasStream.Should().BeTrue();
        canRead.Should().BeTrue();
    }

    [Fact]
    public async Task Consumer_StreamBody_StreamContainsCorrectData()
    {
        CreateFile("body.txt", "hello-stream");
        var endpoint = CreateEndpoint(new()
        {
            ["noop"] = "true", ["delay"] = "100", ["streamBody"] = "true"
        });
        string? readContent = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                var s = (Stream)x.Arg<IExchange>().In.Body!;
                using var reader = new StreamReader(s, leaveOpen: true);
                readContent = reader.ReadToEnd();
            });

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => readContent != null, 3000);
        await consumer.Stop();

        readContent.Should().Be("hello-stream");
    }

    [Fact]
    public async Task Consumer_StreamBody_HeadersStillSet()
    {
        CreateFile("meta.json", "{}");
        var endpoint = CreateEndpoint(new()
        {
            ["noop"] = "true", ["delay"] = "100", ["streamBody"] = "true"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1, 3000);
        await consumer.Stop();

        var ex = processed[0];
        ex.In.Headers[FileHeaders.FileName].Should().Be("meta.json");
        ex.In.Headers[FileHeaders.FileNameOnly].Should().Be("meta");
        ex.In.Headers[FileHeaders.FileExtension].Should().Be(".json");
        ex.In.Headers[FileHeaders.FileLength].Should().BeOfType<long>();
        ex.In.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task Consumer_StreamBody_DeleteAfterRead()
    {
        var filePath = Path.Combine(_inputDir, "todelete.txt");
        CreateFile("todelete.txt", "delete-me");
        var endpoint = CreateEndpoint(new()
        {
            ["delete"] = "true", ["delay"] = "100", ["streamBody"] = "true"
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => !System.IO.File.Exists(filePath), 3000);
        await consumer.Stop();

        System.IO.File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public async Task Consumer_StreamBody_False_ReturnsBytes()
    {
        CreateFile("compat.csv", "back-compat");
        var endpoint = CreateEndpoint(new()
        {
            ["delete"] = "true", ["delay"] = "100"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (FileConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1, 3000);
        await consumer.Stop();

        processed[0].In.Body.Should().BeOfType<byte[]>();
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

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
