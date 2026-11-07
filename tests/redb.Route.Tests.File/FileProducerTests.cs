using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// Tests for FileProducer — file writing, temp files, FileExist strategies.
/// </summary>
public class FileProducerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _outputDir;

    public FileProducerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "redb-route-producer-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _outputDir = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(_outputDir);
    }

    private FileEndpoint CreateEndpoint(Dictionary<string, string>? parameters = null)
    {
        var component = new FileComponent();
        var path = "/" + _outputDir.Replace("\\", "/");
        var parms = parameters ?? new Dictionary<string, string>();
        var uri = new EndpointUri("file", path, $"file://{path}", parms);
        return (FileEndpoint)component.CreateEndpoint(uri);
    }

    private Exchange CreateExchange(string? body = "test content", string? fileName = null)
    {
        var message = new Message { Body = body != null ? System.Text.Encoding.UTF8.GetBytes(body) : null };
        if (fileName != null)
            message.Headers[FileHeaders.FileName] = fileName;
        return new Exchange(message) { Pattern = ExchangePattern.InOnly };
    }

    // ── Basic write ─────────────────────────────────────────────────

    [Fact]
    public async Task Producer_WritesFileWithHeaderName()
    {
        var endpoint = CreateEndpoint();
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("hello world", "output.txt");

        await producer.Process(exchange);

        var path = Path.Combine(_outputDir, "output.txt");
        System.IO.File.Exists(path).Should().BeTrue();
        System.IO.File.ReadAllBytes(path).Should().Equal(System.Text.Encoding.UTF8.GetBytes("hello world"));
    }

    [Fact]
    public async Task Producer_WritesFile_SetsFileNameProducedHeader()
    {
        var endpoint = CreateEndpoint();
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("data", "result.txt");

        await producer.Process(exchange);

        exchange.In.Headers.Should().ContainKey(FileHeaders.FileNameProduced);
        exchange.In.Headers[FileHeaders.FileNameProduced]!.ToString().Should().Contain("result.txt");
    }

    [Fact]
    public async Task Producer_WithoutFileName_GeneratesGuid()
    {
        var endpoint = CreateEndpoint();
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("data");

        await producer.Process(exchange);

        var files = Directory.GetFiles(_outputDir);
        files.Should().HaveCount(1);
        Path.GetFileName(files[0]).Should().StartWith("redb-");
    }

    [Fact]
    public async Task Producer_DynamicFileName()
    {
        var endpoint = CreateEndpoint(new() { ["fileName"] = "report.json" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("{}", null);

        await producer.Process(exchange);

        System.IO.File.Exists(Path.Combine(_outputDir, "report.json")).Should().BeTrue();
    }

    // ── FileExist strategies ────────────────────────────────────────

    [Fact]
    public async Task Producer_FileExist_Override_OverwritesExisting()
    {
        System.IO.File.WriteAllText(Path.Combine(_outputDir, "target.txt"), "old");

        var endpoint = CreateEndpoint(new() { ["fileExist"] = "Override" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("new content", "target.txt");

        await producer.Process(exchange);

        System.IO.File.ReadAllText(Path.Combine(_outputDir, "target.txt")).Should().Be("new content");
    }

    [Fact]
    public async Task Producer_FileExist_Append_AppendsToExisting()
    {
        System.IO.File.WriteAllBytes(Path.Combine(_outputDir, "target.txt"),
            System.Text.Encoding.UTF8.GetBytes("old"));

        var endpoint = CreateEndpoint(new() { ["fileExist"] = "Append" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("NEW", "target.txt");

        await producer.Process(exchange);

        var content = System.IO.File.ReadAllBytes(Path.Combine(_outputDir, "target.txt"));
        System.Text.Encoding.UTF8.GetString(content).Should().Contain("old");
        System.Text.Encoding.UTF8.GetString(content).Should().Contain("NEW");
    }

    [Fact]
    public async Task Producer_FileExist_Append_WithAppendChars()
    {
        System.IO.File.WriteAllText(Path.Combine(_outputDir, "lines.txt"), "line1");

        var endpoint = CreateEndpoint(new() { ["fileExist"] = "Append", ["appendChars"] = "\n" });
        var producer = (FileProducer)endpoint.CreateProducer();

        // Write line2
        var exchange = CreateExchange("line2", "lines.txt");
        await producer.Process(exchange);

        var content = System.IO.File.ReadAllText(Path.Combine(_outputDir, "lines.txt"));
        content.Should().Contain("line2\n");
    }

    [Fact]
    public async Task Producer_FileExist_Fail_ThrowsWhenExists()
    {
        System.IO.File.WriteAllText(Path.Combine(_outputDir, "target.txt"), "existing");

        var endpoint = CreateEndpoint(new() { ["fileExist"] = "Fail" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("new", "target.txt");

        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<IOException>().WithMessage("*FileExist=Fail*");
    }

    [Fact]
    public async Task Producer_FileExist_Ignore_SkipsWrite()
    {
        System.IO.File.WriteAllText(Path.Combine(_outputDir, "target.txt"), "original");

        var endpoint = CreateEndpoint(new() { ["fileExist"] = "Ignore" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("new", "target.txt");

        await producer.Process(exchange);

        System.IO.File.ReadAllText(Path.Combine(_outputDir, "target.txt")).Should().Be("original");
    }

    [Fact]
    public async Task Producer_FileExist_Move_BacksUpExisting()
    {
        System.IO.File.WriteAllText(Path.Combine(_outputDir, "target.txt"), "old");

        var endpoint = CreateEndpoint(new() { ["fileExist"] = "Move" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("new", "target.txt");

        await producer.Process(exchange);

        System.IO.File.ReadAllText(Path.Combine(_outputDir, "target.txt")).Should().Be("new");
        System.IO.File.Exists(Path.Combine(_outputDir, "target.txt.bak")).Should().BeTrue();
        System.IO.File.ReadAllText(Path.Combine(_outputDir, "target.txt.bak")).Should().Be("old");
    }

    // ── Temp file writing ───────────────────────────────────────────

    [Fact]
    public async Task Producer_TempPrefix_WritesToTempThenRenames()
    {
        var endpoint = CreateEndpoint(new() { ["tempPrefix"] = ".redb_" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("atomic data", "result.txt");

        await producer.Process(exchange);

        var targetPath = Path.Combine(_outputDir, "result.txt");
        System.IO.File.Exists(targetPath).Should().BeTrue();
        System.IO.File.ReadAllText(targetPath).Should().Be("atomic data");

        // Temp file should not remain
        var tempPath = Path.Combine(_outputDir, ".redb_result.txt");
        System.IO.File.Exists(tempPath).Should().BeFalse();
    }

    [Fact]
    public async Task Producer_TempFileName_WritesToExactTempFile()
    {
        var endpoint = CreateEndpoint(new() { ["tempFileName"] = ".writing.tmp" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("data", "final.txt");

        await producer.Process(exchange);

        System.IO.File.Exists(Path.Combine(_outputDir, "final.txt")).Should().BeTrue();
        System.IO.File.Exists(Path.Combine(_outputDir, ".writing.tmp")).Should().BeFalse();
    }

    // ── AutoCreate ──────────────────────────────────────────────────

    [Fact]
    public async Task Producer_AutoCreate_CreatesDirectory()
    {
        var subDir = Path.Combine(_outputDir, "nested", "deep");
        var component = new FileComponent();
        var path = "/" + subDir.Replace("\\", "/");
        var uri = new EndpointUri("file", path, $"file://{path}", new Dictionary<string, string> { ["autoCreate"] = "true" });
        var ep = (FileEndpoint)component.CreateEndpoint(uri);

        var producer = (FileProducer)ep.CreateProducer();
        var exchange = CreateExchange("nested data", "file.txt");

        await producer.Process(exchange);

        System.IO.File.Exists(Path.Combine(subDir, "file.txt")).Should().BeTrue();
    }

    // ── AllowNullBody ───────────────────────────────────────────────

    [Fact]
    public async Task Producer_NullBody_AllowNullBodyFalse_Throws()
    {
        var endpoint = CreateEndpoint(new() { ["allowNullBody"] = "false" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange(null, "empty.txt");

        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*null*AllowNullBody*");
    }

    [Fact]
    public async Task Producer_NullBody_AllowNullBodyTrue_WritesEmptyFile()
    {
        var endpoint = CreateEndpoint(new() { ["allowNullBody"] = "true" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange(null, "empty.txt");

        await producer.Process(exchange);

        var path = Path.Combine(_outputDir, "empty.txt");
        System.IO.File.Exists(path).Should().BeTrue();
        System.IO.File.ReadAllBytes(path).Should().BeEmpty();
    }

    // ── String body ─────────────────────────────────────────────────

    [Fact]
    public async Task Producer_StringBody_WritesAsText()
    {
        var endpoint = CreateEndpoint();
        var producer = (FileProducer)endpoint.CreateProducer();
        var message = new Message { Body = "text content" };
        message.Headers[FileHeaders.FileName] = "text.txt";
        var exchange = new Exchange(message);

        await producer.Process(exchange);

        System.IO.File.ReadAllText(Path.Combine(_outputDir, "text.txt")).Should().Be("text content");
    }

    // ── Stream body ─────────────────────────────────────────────────

    [Fact]
    public async Task Producer_StreamBody_WritesFromStream()
    {
        var endpoint = CreateEndpoint();
        var producer = (FileProducer)endpoint.CreateProducer();
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("stream data"));
        var message = new Message { Body = stream };
        message.Headers[FileHeaders.FileName] = "stream.txt";
        var exchange = new Exchange(message);

        await producer.Process(exchange);

        System.IO.File.ReadAllText(Path.Combine(_outputDir, "stream.txt")).Should().Be("stream data");
    }

    // ── EagerDeleteTargetFile ───────────────────────────────────────

    [Fact]
    public async Task Producer_EagerDeleteTargetFile_False_DoesNotPreDelete()
    {
        System.IO.File.WriteAllText(Path.Combine(_outputDir, "target.txt"), "old");

        var endpoint = CreateEndpoint(new() { ["eagerDeleteTargetFile"] = "false" });
        var producer = (FileProducer)endpoint.CreateProducer();
        var exchange = CreateExchange("new", "target.txt");

        await producer.Process(exchange);

        // File should still be overwritten (WriteAllBytes handles it)
        System.IO.File.ReadAllText(Path.Combine(_outputDir, "target.txt")).Should().Be("new");
    }

    // ── Start/Stop ──────────────────────────────────────────────────

    [Fact]
    public async Task Producer_StartStop_NoOp()
    {
        var endpoint = CreateEndpoint();
        var producer = endpoint.CreateProducer();

        await producer.Start();
        await producer.Stop();
        // No exception
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
