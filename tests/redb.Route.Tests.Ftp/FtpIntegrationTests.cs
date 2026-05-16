using System.Collections.Concurrent;
using System.Text;
using FluentFTP;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Ftp;
using Xunit.Abstractions;

namespace redb.Route.Tests.Ftp;

/// <summary>
/// Integration tests against delfer/alpine-ftp-server docker container.
/// Expects FTP at localhost:21 (testuser/secret).
/// Start with: docker compose -f docker-compose.tests.yml up ftp -d
/// </summary>
[Trait("Category", "Integration")]
public sealed class FtpIntegrationTests : IAsyncLifetime
{
    private const string Host = "localhost";
    private const int Port = 21;
    private const string Username = "testuser";
    private const string Password = "secret";

    private readonly ITestOutputHelper _output;

    public FtpIntegrationTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    // ───── Helpers ─────

    private static string UniqueDir() => $"test-{Guid.NewGuid():N}";

    private FtpEndpoint CreateEndpoint(string remotePath, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&username={Username}&password={Password}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"ftp:///{remotePath}?{qs}");
        return (FtpEndpoint)new FtpComponent().CreateEndpoint(uri);
    }

    private async Task<AsyncFtpClient> CreateRawClientAsync()
    {
        var client = new AsyncFtpClient(Host, Username, Password, Port);
        await client.Connect().ConfigureAwait(false);
        return client;
    }

    private async Task CleanupDirAsync(AsyncFtpClient client, string path)
    {
        if (!await client.DirectoryExists(path).ConfigureAwait(false)) return;

        var items = await client.GetListing(path, FtpListOption.AllFiles).ConfigureAwait(false);
        foreach (var item in items)
        {
            if (item.Type == FtpObjectType.Directory)
                await CleanupDirAsync(client, item.FullName).ConfigureAwait(false);
            else
                await client.DeleteFile(item.FullName).ConfigureAwait(false);
        }
        await client.DeleteDirectory(path).ConfigureAwait(false);
    }

    private async Task SeedFileAsync(AsyncFtpClient client, string path, string content)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await client.UploadStream(ms, path, FtpRemoteExists.Overwrite, true).ConfigureAwait(false);
    }

    private async Task<string> ReadFileAsync(AsyncFtpClient client, string path)
    {
        using var ms = new MemoryStream();
        await client.DownloadStream(ms, path).ConfigureAwait(false);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ───── Producer Tests ─────

    [Fact]
    public async Task Producer_UploadsTextFile()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";
        _output.WriteLine($"Remote: {remotePath}");

        var ep = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello FTP!"));
        exchange.In.Headers[FtpHeaders.FileName] = "hello.txt";
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers[FtpHeaders.FileNameProduced].Should().NotBeNull();

        using var client = await CreateRawClientAsync();
        var filePath = $"/{remotePath}/hello.txt";
        (await client.FileExists(filePath)).Should().BeTrue();
        var content = await ReadFileAsync(client, filePath);
        content.Should().Be("Hello FTP!");

        await CleanupDirAsync(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_UploadsBinaryFile()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var data = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x42, 0x99 };
        var exchange = new Exchange(new Message(data));
        exchange.In.Headers[FtpHeaders.FileName] = "binary.bin";
        await producer.Process(exchange);
        await producer.Stop();

        using var client = await CreateRawClientAsync();
        using var ms = new MemoryStream();
        await client.DownloadStream(ms, $"/{remotePath}/binary.bin");
        ms.ToArray().Should().BeEquivalentTo(data);

        await CleanupDirAsync(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_TempPrefix_AtomicUpload()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&tempPrefix=.redb_");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("atomic content"));
        exchange.In.Headers[FtpHeaders.FileName] = "atomic.txt";
        await producer.Process(exchange);
        await producer.Stop();

        using var client = await CreateRawClientAsync();
        (await client.FileExists($"/{remotePath}/atomic.txt")).Should().BeTrue();

        // No temp files left
        var items = await client.GetListing($"/{remotePath}");
        items.Where(i => i.Name.StartsWith(".redb_")).Should().BeEmpty();

        await CleanupDirAsync(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_FileExistFail_ThrowsOnDuplicate()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&fileExist=Fail");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var ex1 = new Exchange(new Message("first"));
        ex1.In.Headers[FtpHeaders.FileName] = "dup.txt";
        await producer.Process(ex1);

        var ex2 = new Exchange(new Message("second"));
        ex2.In.Headers[FtpHeaders.FileName] = "dup.txt";
        var act = () => producer.Process(ex2);
        await act.Should().ThrowAsync<IOException>();
        await producer.Stop();

        using var client = await CreateRawClientAsync();
        await CleanupDirAsync(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_FileExistIgnore_KeepsOriginal()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&fileExist=Ignore");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var ex1 = new Exchange(new Message("original"));
        ex1.In.Headers[FtpHeaders.FileName] = "keep.txt";
        await producer.Process(ex1);

        var ex2 = new Exchange(new Message("ignored"));
        ex2.In.Headers[FtpHeaders.FileName] = "keep.txt";
        await producer.Process(ex2);
        await producer.Stop();

        using var client = await CreateRawClientAsync();
        var content = await ReadFileAsync(client, $"/{remotePath}/keep.txt");
        content.Should().Be("original");

        await CleanupDirAsync(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_FileExistOverride_Overwrites()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&fileExist=Override");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var ex1 = new Exchange(new Message("v1"));
        ex1.In.Headers[FtpHeaders.FileName] = "over.txt";
        await producer.Process(ex1);

        var ex2 = new Exchange(new Message("v2"));
        ex2.In.Headers[FtpHeaders.FileName] = "over.txt";
        await producer.Process(ex2);
        await producer.Stop();

        using var client = await CreateRawClientAsync();
        var content = await ReadFileAsync(client, $"/{remotePath}/over.txt");
        content.Should().Be("v2");

        await CleanupDirAsync(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_AutoCreateNestedDirs()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("deep"));
        exchange.In.Headers[FtpHeaders.FileName] = "sub/nested/deep.txt";
        await producer.Process(exchange);
        await producer.Stop();

        using var client = await CreateRawClientAsync();
        (await client.FileExists($"/{remotePath}/sub/nested/deep.txt")).Should().BeTrue();

        await CleanupDirAsync(client, $"/{remotePath}");
    }

    // ───── Consumer Tests ─────

    [Fact]
    public async Task Consumer_ReceivesUploadedFile()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/sample.txt", "Hello from FTP");

        var ep = CreateEndpoint(remotePath, "noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[FtpHeaders.FileName].Should().Be("sample.txt");
        rx.In.Headers[FtpHeaders.FileNameOnly].Should().Be("sample");
        rx.In.Headers[FtpHeaders.FileExtension].Should().Be(".txt");
        rx.In.Headers[FtpHeaders.Host].Should().Be(Host);
        rx.In.Headers[FtpHeaders.Port].Should().Be(Port);
        rx.In.Headers[FtpHeaders.Username].Should().Be(Username);
        ((long)rx.In.Headers[FtpHeaders.FileLength]!).Should().BeGreaterThan(0);

        var body = Encoding.UTF8.GetString((byte[])rx.In.Body!);
        body.Should().Be("Hello from FTP");

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_Delete_RemovesFileAfterProcessing()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/deleteme.txt", "delete me");

        var ep = CreateEndpoint(remotePath, "delete=true&delay=500&initialDelay=100");
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        (await rawClient.FileExists($"{fullDir}/deleteme.txt")).Should().BeFalse();

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_MoveTo_MovesFileAfterProcessing()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await rawClient.CreateDirectory($"{fullDir}/.done", true);
        await SeedFileAsync(rawClient, $"{fullDir}/moveme.txt", "move me");

        var ep = CreateEndpoint(remotePath, $"moveTo=.done&delay=500&initialDelay=100");
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        (await rawClient.FileExists($"{fullDir}/moveme.txt")).Should().BeFalse();
        (await rawClient.FileExists($"{fullDir}/.done/moveme.txt")).Should().BeTrue();

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_IncludeFilter_FiltersFiles()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/report.csv", "csv data");
        await SeedFileAsync(rawClient, $"{fullDir}/notes.txt", "text data");

        var ep = CreateEndpoint(remotePath, "include=*.csv&noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        ((string)received.First().In.Headers[FtpHeaders.FileName]!).Should().Be("report.csv");

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_ExcludeFilter_SkipsMatchingFiles()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/keep.txt", "keep");
        await SeedFileAsync(rawClient, $"{fullDir}/skip.log", "skip");

        var ep = CreateEndpoint(remotePath, "exclude=*.log&noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        ((string)received.First().In.Headers[FtpHeaders.FileName]!).Should().Be("keep.txt");

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_Recursive_ReadsSubdirectories()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await rawClient.CreateDirectory($"{fullDir}/sub", true);
        await SeedFileAsync(rawClient, $"{fullDir}/root.txt", "root");
        await SeedFileAsync(rawClient, $"{fullDir}/sub/child.txt", "child");

        var ep = CreateEndpoint(remotePath,
            "recursive=true&noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var counter = 0;
        var done = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                if (Interlocked.Increment(ref counter) >= 2) done.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(2);
        received.Select(r => (string)r.In.Headers[FtpHeaders.FileName]!)
            .Should().BeEquivalentTo(["root.txt", "child.txt"]);

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_MaxMessagesPerPoll_LimitsFiles()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        for (int i = 0; i < 5; i++)
            await SeedFileAsync(rawClient, $"{fullDir}/file-{i}.txt", $"file-{i}");

        var ep = CreateEndpoint(remotePath,
            "maxMessagesPerPoll=2&noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var counter = 0;
        var done = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                if (Interlocked.Increment(ref counter) >= 2) done.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(10_000));
        await Task.Delay(200);
        await consumer.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(2);

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_Idempotent_NoReprocessing()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/stable.txt", "idempotent data");

        var ep = CreateEndpoint(remotePath,
            "idempotent=true&noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await Task.Delay(2000);
        await consumer.Stop();

        received.Should().HaveCount(1);

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_EmptyDirectory_NoMessages()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        await rawClient.CreateDirectory($"/{remotePath}", true);

        var ep = CreateEndpoint(remotePath, "noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(2000);
        await consumer.Stop();

        received.Should().BeEmpty();

        await CleanupDirAsync(rawClient, $"/{remotePath}");
    }

    [Fact]
    public async Task Consumer_SortByName_OrderedOutput()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);

        foreach (var name in new[] { "charlie.txt", "alpha.txt", "bravo.txt" })
            await SeedFileAsync(rawClient, $"{fullDir}/{name}", name);

        var ep = CreateEndpoint(remotePath,
            "sortBy=Name&noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var counter = 0;
        var done = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                if (Interlocked.Increment(ref counter) >= 3) done.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(3);
        var names = received.Select(r => (string)r.In.Headers[FtpHeaders.FileName]!)
            .ToList();
        names.Should().BeEquivalentTo(["alpha.txt", "bravo.txt", "charlie.txt"]);

        await CleanupDirAsync(rawClient, fullDir);
    }

    // ───── Roundtrip Tests ─────

    [Fact]
    public async Task Producer_To_Consumer_FullRoundtrip()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";
        _output.WriteLine($"Roundtrip dir: {remotePath}");

        var prodEp = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (FtpProducer)prodEp.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("roundtrip content"));
        exchange.In.Headers[FtpHeaders.FileName] = "roundtrip.txt";
        await producer.Process(exchange);
        await producer.Stop();

        var consEp = CreateEndpoint(remotePath,
            "delete=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)consEp.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[FtpHeaders.FileName].Should().Be("roundtrip.txt");
        var body = Encoding.UTF8.GetString((byte[])rx.In.Body!);
        body.Should().Be("roundtrip content");

        using var rawClient = await CreateRawClientAsync();
        (await rawClient.FileExists($"/{remotePath}/roundtrip.txt")).Should().BeFalse();

        await CleanupDirAsync(rawClient, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_To_Consumer_MultipleFiles()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";
        const int fileCount = 5;

        var prodEp = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (FtpProducer)prodEp.CreateProducer();
        await producer.Start();

        for (int i = 0; i < fileCount; i++)
        {
            var ex = new Exchange(new Message($"content-{i}"));
            ex.In.Headers[FtpHeaders.FileName] = $"file-{i}.txt";
            await producer.Process(ex);
        }
        await producer.Stop();

        var consEp = CreateEndpoint(remotePath,
            "noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var allDone = new TaskCompletionSource();
        var counter = 0;

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                if (Interlocked.Increment(ref counter) >= fileCount) allDone.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)consEp.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(fileCount);

        using var rawClient = await CreateRawClientAsync();
        await CleanupDirAsync(rawClient, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_NullBody_WithAllowNullBody_UploadsEmptyFile()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&allowNullBody=true");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = null });
        exchange.In.Headers[FtpHeaders.FileName] = "empty.txt";
        await producer.Process(exchange);
        await producer.Stop();

        using var client = await CreateRawClientAsync();
        var filePath = $"/{remotePath}/empty.txt";
        (await client.FileExists(filePath)).Should().BeTrue();

        using var ms = new MemoryStream();
        await client.DownloadStream(ms, filePath);
        ms.ToArray().Length.Should().Be(0);

        await CleanupDirAsync(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_NullBody_WithoutAllowNullBody_Throws()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&allowNullBody=false");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = null });
        exchange.In.Headers[FtpHeaders.FileName] = "null.txt";

        var act = () => producer.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AllowNullBody*");
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_FileExistMove_BacksUpExisting()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        var ep = CreateEndpoint(remotePath,
            "autoCreate=true&fileExist=Move&moveExistingFileStrategy=Backup");
        var producer = (FtpProducer)ep.CreateProducer();
        await producer.Start();

        var ex1 = new Exchange(new Message("v1"));
        ex1.In.Headers[FtpHeaders.FileName] = "versioned.txt";
        await producer.Process(ex1);

        var ex2 = new Exchange(new Message("v2"));
        ex2.In.Headers[FtpHeaders.FileName] = "versioned.txt";
        await producer.Process(ex2);
        await producer.Stop();

        using var client = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        (await client.FileExists($"{fullDir}/versioned.txt")).Should().BeTrue();
        var content = await ReadFileAsync(client, $"{fullDir}/versioned.txt");
        content.Should().Be("v2");

        var items = await client.GetListing(fullDir);
        items.Where(f => f.Name.StartsWith("versioned.txt.bak")).Should().HaveCount(1);

        await CleanupDirAsync(client, fullDir);
    }

    [Fact]
    public async Task Consumer_PreMove_MovesBeforeProcessing()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/source.txt", "pre-move data");

        var ep = CreateEndpoint(remotePath,
            "preMove=.inprogress&noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);

        (await rawClient.FileExists($"{fullDir}/source.txt")).Should().BeFalse();
        (await rawClient.FileExists($"{fullDir}/.inprogress/source.txt")).Should().BeTrue();

        await CleanupDirAsync(rawClient, fullDir);
    }

    // ───── StreamBody Tests ─────

    [Fact]
    public async Task Consumer_StreamBody_ReceivesStream()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/streamed.txt", "stream content");

        var ep = CreateEndpoint(remotePath, "streamBody=true&noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Body.Should().BeAssignableTo<Stream>();

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_StreamBody_DataCorrect()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";
        var expectedContent = "hello from ftp stream";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/readable.txt", expectedContent);

        var ep = CreateEndpoint(remotePath, "streamBody=true&noop=true&delay=500&initialDelay=100");
        string? readContent = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var exchange = ci.ArgAt<IExchange>(0);
                var stream = (Stream)exchange.In.Body!;
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                readContent = await reader.ReadToEndAsync();
                tcs.TrySetResult();
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        readContent.Should().Be(expectedContent);

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_StreamBody_HeadersPopulated()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/meta.txt", "headers test");

        var ep = CreateEndpoint(remotePath, "streamBody=true&noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[FtpHeaders.FileName].Should().Be("meta.txt");
        rx.In.Headers[FtpHeaders.FileNameOnly].Should().Be("meta");
        rx.In.Headers[FtpHeaders.FileExtension].Should().Be(".txt");
        rx.In.Headers[FtpHeaders.Host].Should().Be(Host);
        rx.In.Headers[FtpHeaders.Port].Should().Be(Port);
        rx.In.Headers[FtpHeaders.Username].Should().Be(Username);
        ((long)rx.In.Headers[FtpHeaders.FileLength]!).Should().BeGreaterThan(0);

        await CleanupDirAsync(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_StreamBody_DeleteAfterRead()
    {
        var dir = UniqueDir();
        var remotePath = $"{dir}";

        using var rawClient = await CreateRawClientAsync();
        var fullDir = $"/{remotePath}";
        await rawClient.CreateDirectory(fullDir, true);
        await SeedFileAsync(rawClient, $"{fullDir}/deletable.txt", "delete after stream");

        var ep = CreateEndpoint(remotePath, "streamBody=true&delete=true&delay=500&initialDelay=100");
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (FtpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        (await rawClient.FileExists($"{fullDir}/deletable.txt")).Should().BeFalse();

        await CleanupDirAsync(rawClient, fullDir);
    }
}
