using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.GenericFile;

namespace redb.Route.Tests.GenericFile;

/// <summary>
/// The file keys in the consumer's own options (findings register row 34 point 5): every key of
/// <see cref="FileLanguage.Keys"/> resolves against the file being handled, ignoring case as these options always
/// have; an unknown key fails when the endpoint is created. Before 4.7.0 only <c>name</c> and <c>name.noext</c>
/// resolved, and any other key stayed in the path as written.
/// </summary>
public class FileKeysInOptionsTests : GenericFileTestBase
{
    private static readonly DateTimeOffset Modified = new(2026, 11, 5, 10, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("done-${file:name}", "/in/done-order.CSV/order.CSV")]
    [InlineData("done-${file:onlyname.noext}", "/in/done-order/order.CSV")]
    [InlineData("done-${file:ext}", "/in/done-csv/order.CSV")]
    [InlineData("done-${file:name.ext}", "/in/done-csv/order.CSV")]
    [InlineData("done-${file:size}", "/in/done-7/order.CSV")]
    [InlineData("done-${FILE:EXT}", "/in/done-csv/order.CSV")]
    [InlineData("done-${file:Name.NoExt}", "/in/done-order/order.CSV")]
    public async Task MoveTo_ResolvesEveryKey(string moveTo, string expected)
    {
        Ops.AddFile("/in/order.CSV", "PAYLOAD", Modified);
        var consumer = (TestFileConsumer)Endpoint(new() { ["moveTo"] = moveTo }).CreateConsumer(Collector().Processor);

        await consumer.PollOnceAsync();

        Ops.HasFile(expected).Should().BeTrue();
        Ops.AllDirectories().Should().NotContain(d => d.Contains("${"));
    }

    [Fact]
    public async Task MoveTo_ModifiedIsTheFileTime()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD", Modified);
        var consumer = (TestFileConsumer)Endpoint(new() { ["moveTo"] = "m-${file:modified}" }).CreateConsumer(Collector().Processor);

        await consumer.PollOnceAsync();

        Ops.HasFile("/in/m-2026-11-05T10:30:00.0000000+00:00/order.csv").Should().BeTrue();
    }

    [Fact]
    public async Task DoneFileName_ResolvesTheExtension()
    {
        Ops.AddFile("/in/order.csv", "PAYLOAD");
        Ops.AddFile("/in/order.csv-ready", "");
        var (processor, bodies, _) = Collector();
        var consumer = (TestFileConsumer)Endpoint(new() { ["doneFileName"] = "${file:name.noext}.${file:ext}-ready", ["include"] = "*.csv", ["noop"] = "true" })
            .CreateConsumer(processor);

        await consumer.PollOnceAsync();

        bodies.Should().ContainSingle().Which.Should().Be("PAYLOAD");
    }

    [Theory]
    [InlineData("moveTo")]
    [InlineData("preMove")]
    [InlineData("doneFileName")]
    [InlineData("idempotentKey")]
    [InlineData("tempFileName")]
    public void AnUnknownKey_FailsWhenTheEndpointIsCreated(string option)
    {
        var act = () => Endpoint(new() { [option] = "x-${file:nosuch}" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"{option}='x-${{file:nosuch}}': 'nosuch' is not a file key; the keys are name, onlyname, name.noext, *");
    }

    [Theory]
    [InlineData(".writing.tmp", "/in/.writing.tmp")]
    [InlineData("${file:name}.part", "/in/order.xml.part")]
    [InlineData("${File:Name.NoExt}.tmp", "/in/order.tmp")]
    public async Task TempFileName_IsTheTempFileTheWriteRenamesFrom(string tempFileName, string expected)
    {
        // Row 46: the shared write flow of every file producer (File, FTP, SFTP); the keys read the target file.
        string? renamedFrom = null;
        Ops.FailMove = source => { renamedFrom = source; return null; };
        var producer = Endpoint(new() { ["tempFileName"] = tempFileName }).CreateProducer();
        var message = new redb.Route.Core.Message { Body = System.Text.Encoding.UTF8.GetBytes("PAYLOAD") };
        message.Headers["testFile.Name"] = "order.xml";

        await producer.Process(new redb.Route.Core.Exchange(message) { Pattern = ExchangePattern.InOnly });

        renamedFrom.Should().Be(expected);
        Ops.TextAt("/in/order.xml").Should().Be("PAYLOAD");
        Ops.AllFiles().Should().NotContain(expected);
    }

    [Fact]
    public void SubstituteFileTokens_ByNameAlone_KnowsTheNameKeys()
    {
        GenericFileUtils.SubstituteFileTokens("order.csv", "${file:name.noext}.${file:ext}|${file:size}", Ops)
            .Should().Be("order.csv|", "the name form knows no size");
    }

    [Fact]
    public void Facts_AreTheValuesTheFileHeadersCarry()
    {
        var file = new GenericFileInfo { Name = "a.XML", FullPath = "/in/sub/a.XML", BasePath = "/in", Length = 42, LastModified = Modified };

        var facts = new GenericFileFacts(file, "/in/sub/a.XML", Ops);

        facts.Name.Should().Be("a.XML");
        facts.NameOnly.Should().Be("a");
        facts.Extension.Should().Be("xml", "the header holds '.xml', the key is without the dot");
        facts.Parent.Should().Be("/in/sub");
        facts.AbsolutePath.Should().Be("/in/sub/a.XML");
        facts.RelativePath.Should().Be(Ops.GetRelativePath("/in", "/in/sub/a.XML"));
        facts.Length.Should().Be(42);
        facts.LastModified.Should().Be(Modified);
    }

    // ── The compatibility form of the hooks ─────────────────────────

    [Fact]
    public async Task ASubclassOverridingThePathForm_IsStillCalled()
    {
        Ops.AddFile("/in/ok.csv", "OK");
        Ops.AddFile("/in/bad.csv", "BAD");
        var endpoint = Endpoint(new() { ["noop"] = "true" });
        var (processor, _, _) = Collector(exchange =>
        {
            if (exchange.In.Headers["testFile.Name"] as string == "bad.csv")
                exchange.Exception = new FormatException("bad");
        });
        var consumer = new PathFormConsumer(endpoint, processor, new TestFileEndpointOptions { Noop = true }, Ops);

        await consumer.PollOnceAsync();

        consumer.Calls.Should().BeEquivalentTo(["post /in/ok.csv ok.csv /in", "failed /in/bad.csv bad.csv /in"]);
    }

    [Fact]
    public async Task ASubclassOverridingBothForms_GetsTheFileForm()
    {
        Ops.AddFile("/in/ok.csv", "OK");
        var endpoint = Endpoint(new() { ["noop"] = "true" });
        var consumer = new BothFormsConsumer(endpoint, Collector().Processor, new TestFileEndpointOptions { Noop = true }, Ops);

        await consumer.PollOnceAsync();

        consumer.Calls.Should().Equal("post-file ok.csv 2");
    }

    private class PathFormConsumer(TestFileEndpoint endpoint, IProcessor processor, TestFileEndpointOptions options, FakeFileOperations ops)
        : GenericFileConsumer<TestFileEndpointOptions>(endpoint, processor, options, ops)
    {
        public List<string> Calls { get; } = [];

        protected override string ConsumerName => "path-form";

        protected override string BasePath => endpoint.DirectoryPath;

        public Task<PollTally> PollOnceAsync() => PollDirectory(CancellationToken.None);

        protected override void SetExchangeHeaders(IMessage message, GenericFileInfo file, string workPath)
            => message.Headers["testFile.Name"] = file.Name;

        protected override Task<bool> AcquireReadLockAsync(GenericFileInfo file, CancellationToken ct) => Task.FromResult(true);

        protected override Task PostProcessAsync(string filePath, string fileName, string basePath, CancellationToken ct)
        {
            Calls.Add($"post {filePath} {fileName} {basePath}");
            return Task.CompletedTask;
        }

        protected override Task OnProcessingFailedAsync(string filePath, string fileName, string basePath, CancellationToken ct)
        {
            Calls.Add($"failed {filePath} {fileName} {basePath}");
            return Task.CompletedTask;
        }
    }

    private sealed class BothFormsConsumer(TestFileEndpoint endpoint, IProcessor processor, TestFileEndpointOptions options, FakeFileOperations ops)
        : PathFormConsumer(endpoint, processor, options, ops)
    {
        protected override Task PostProcessAsync(GenericFileInfo file, string workPath, CancellationToken ct)
        {
            Calls.Add($"post-file {file.Name} {file.Length}");
            return Task.CompletedTask;
        }

        protected override Task PostProcessAsync(string filePath, string fileName, string basePath, CancellationToken ct)
        {
            Calls.Add("post-path");
            return Task.CompletedTask;
        }
    }
}
