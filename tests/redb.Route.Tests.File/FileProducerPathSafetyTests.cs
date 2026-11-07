using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// The producer's target file name normally arrives from the incoming message, so it is
/// untrusted input. These tests pin the jail: the producer must not write outside its
/// endpoint directory unless that was asked for explicitly.
/// </summary>
public class FileProducerPathSafetyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _outputDir;

    public FileProducerPathSafetyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "redb-route-jail-" + Guid.NewGuid().ToString("N")[..8]);
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private FileEndpoint CreateEndpoint(Dictionary<string, string>? parameters = null)
    {
        var component = new FileComponent();
        var path = "/" + _outputDir.Replace("\\", "/");
        var uri = new EndpointUri("file", path, $"file://{path}", parameters ?? new Dictionary<string, string>());
        return (FileEndpoint)component.CreateEndpoint(uri);
    }

    private static IExchange ExchangeWithFileName(FileEndpoint endpoint, string fileName, string body = "PAYLOAD")
    {
        var message = new Message { Body = body };
        message.Headers[FileHeaders.FileName] = fileName;
        return Exchange.Create(message, endpoint.ScopeFactory);
    }

    [Fact]
    public async Task RelativeEscape_FromHeader_IsRejected()
    {
        var endpoint = CreateEndpoint();
        var producer = endpoint.CreateProducer();

        var act = () => producer.Process(ExchangeWithFileName(endpoint, "../escaped.txt"));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        System.IO.File.Exists(Path.Combine(_tempDir, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task AbsolutePath_FromHeader_IsRejected()
    {
        var endpoint = CreateEndpoint();
        var producer = endpoint.CreateProducer();
        var absoluteTarget = Path.Combine(_tempDir, "absolute-escape.txt");

        var act = () => producer.Process(ExchangeWithFileName(endpoint, absoluteTarget));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        System.IO.File.Exists(absoluteTarget).Should().BeFalse();
    }

    [Fact]
    public async Task SiblingDirectoryWithSharedPrefix_IsRejected()
    {
        // "out2" starts with "out": a plain prefix comparison would let this through.
        var endpoint = CreateEndpoint();
        var producer = endpoint.CreateProducer();

        var act = () => producer.Process(ExchangeWithFileName(endpoint, "../out2/escaped.txt"));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        System.IO.File.Exists(Path.Combine(_tempDir, "out2", "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task RelativeEscape_FromFileNameOption_IsRejected()
    {
        var endpoint = CreateEndpoint(new() { ["fileName"] = "../escaped-by-option.txt" });
        var producer = endpoint.CreateProducer();

        var message = new Message { Body = "PAYLOAD" };
        var act = () => producer.Process(Exchange.Create(message, endpoint.ScopeFactory));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        System.IO.File.Exists(Path.Combine(_tempDir, "escaped-by-option.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task SubdirectoryInsideTheEndpoint_IsAllowed()
    {
        var endpoint = CreateEndpoint();
        var producer = endpoint.CreateProducer();

        await producer.Process(ExchangeWithFileName(endpoint, "sub/ok.txt"));

        System.IO.File.Exists(Path.Combine(_outputDir, "sub", "ok.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task PlainFileName_IsAllowed()
    {
        var endpoint = CreateEndpoint();
        var producer = endpoint.CreateProducer();

        await producer.Process(ExchangeWithFileName(endpoint, "report.csv"));

        System.IO.File.ReadAllText(Path.Combine(_outputDir, "report.csv")).Should().Be("PAYLOAD");
    }

    [Fact]
    public async Task JailCanBeTurnedOffExplicitly()
    {
        var endpoint = CreateEndpoint(new() { ["jailStartingDirectory"] = "false" });
        var producer = endpoint.CreateProducer();

        await producer.Process(ExchangeWithFileName(endpoint, "../deliberate.txt"));

        System.IO.File.Exists(Path.Combine(_tempDir, "deliberate.txt")).Should()
            .BeTrue("writing outside must stay possible when it is asked for explicitly");
    }
}
