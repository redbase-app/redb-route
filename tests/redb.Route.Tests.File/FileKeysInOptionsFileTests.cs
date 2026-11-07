using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// The file keys in the File consumer's options on a real disk (findings register row 34 point 5): <c>moveTo</c> and
/// <c>moveFailed</c> resolve every key, and a key gives the same value in the option as in a template over the
/// <c>redbFile.*</c> headers of the same file.
/// </summary>
public sealed class FileKeysInOptionsFileTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "redb-route-filekeys-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _inputDir;

    public FileKeysInOptionsFileTests()
    {
        _inputDir = Path.Combine(_tempDir, "input");
        Directory.CreateDirectory(_inputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private FileEndpoint Endpoint(Dictionary<string, string> parameters)
    {
        var path = "/" + _inputDir.Replace("\\", "/");
        parameters["delay"] = "100";
        return (FileEndpoint)new FileComponent().CreateEndpoint(new EndpointUri("file", path, $"file://{path}", parameters));
    }

    private static async Task Until(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 5000)
            await Task.Delay(50);
    }

    [Fact]
    public async Task MoveTo_ResolvesTheKeys_AsTheTemplateDoesOverTheHeaders()
    {
        System.IO.File.WriteAllText(Path.Combine(_inputDir, "order.CSV"), "1234567");
        string? fromTemplate = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask)
            .AndDoes(call => fromTemplate = ExpressionResolver.ProcessTemplate("done-${file:ext}-${file:size}", call.Arg<IExchange>()));
        var consumer = (FileConsumer)Endpoint(new() { ["moveTo"] = "done-${file:ext}-${file:size}" }).CreateConsumer(processor);

        await consumer.Start();
        var target = Path.Combine(_inputDir, "done-csv-7", "order.CSV");
        await Until(() => System.IO.File.Exists(target));
        await consumer.Stop();

        System.IO.File.Exists(target).Should().BeTrue();
        fromTemplate.Should().Be("done-csv-7", "the option and the template read the same file");
    }

    [Fact]
    public async Task MoveFailed_ResolvesTheKeys()
    {
        System.IO.File.WriteAllText(Path.Combine(_inputDir, "bad.xml"), "x");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask)
            .AndDoes(call => call.Arg<IExchange>().Exception = new FormatException("bad"));
        var consumer = (FileConsumer)Endpoint(new() { ["moveFailed"] = "failed-${file:ext}-${file:length}" }).CreateConsumer(processor);

        await consumer.Start();
        var target = Path.Combine(_inputDir, "failed-xml-1", "bad.xml");
        await Until(() => System.IO.File.Exists(target));
        await consumer.Stop();

        System.IO.File.Exists(target).Should().BeTrue();
    }

    [Fact]
    public void MoveFailed_WithAnUnknownKey_FailsWhenTheEndpointIsCreated()
    {
        var act = () => Endpoint(new() { ["moveFailed"] = "failed/${file:nosuch}" });

        act.Should().Throw<InvalidOperationException>().WithMessage("moveFailed='failed/${file:nosuch}': 'nosuch' is not a file key*");
    }

    // Row 46: tempFileName of the producer reads the file keys of the target file, the name being written.

    private sealed class TempProbe(FileEndpoint endpoint) : FileProducer(endpoint, endpoint.EndpointOptions)
    {
        public string Temp(string targetPath) => ResolveTempPath(targetPath);
    }

    private TempProbe Producer(string tempFileName) => new(Endpoint(new() { ["tempFileName"] = tempFileName }));

    [Fact]
    public void TempFileName_WithoutKeys_IsThePathAsWritten()
    {
        var target = Path.Combine(_inputDir, "order.xml");

        Producer(".writing.tmp").Temp(target).Should().Be(Path.Combine(_inputDir, ".writing.tmp"));
    }

    [Theory]
    [InlineData("${file:name}.part", "order.xml.part")]
    [InlineData("${file:name.noext}.tmp", "order.tmp")]
    [InlineData("tmp-${file:ext}-${file:onlyname}", "tmp-xml-order.xml")]
    [InlineData("${FILE:NAME.NOEXT}.tmp", "order.tmp")]
    public void TempFileName_ResolvesTheKeysOfTheTargetFile(string tempFileName, string expected)
    {
        var target = Path.Combine(_inputDir, "order.xml");

        Producer(tempFileName).Temp(target).Should().Be(Path.Combine(_inputDir, expected));
    }

    [Fact]
    public async Task TempFileName_WithKeys_WritesThroughTheTempFileAndRenames()
    {
        var producer = Endpoint(new() { ["tempFileName"] = "${file:name.noext}.part" }).CreateProducer();
        var message = new Message { Body = System.Text.Encoding.UTF8.GetBytes("data") };
        message.Headers[FileHeaders.FileName] = "final.txt";

        await producer.Process(new Exchange(message) { Pattern = ExchangePattern.InOnly });

        System.IO.File.ReadAllText(Path.Combine(_inputDir, "final.txt")).Should().Be("data");
        System.IO.File.Exists(Path.Combine(_inputDir, "final.part")).Should().BeFalse();
    }

    [Fact]
    public void TempFileName_WithAnUnknownKey_FailsWhenTheEndpointIsCreated()
    {
        var act = () => Endpoint(new() { ["tempFileName"] = "${file:nosuch}.tmp" });

        act.Should().Throw<InvalidOperationException>().WithMessage("tempFileName='${file:nosuch}.tmp': 'nosuch' is not a file key*");
    }
}
