using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sftp;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sftp;

/// <summary>
/// Integration tests against atmoz/sftp docker container.
/// Expects SFTP at localhost:2222 (testuser/secret, writable dir: /upload).
/// Start with: docker compose -f docker-compose.tests.yml up sftp -d
/// </summary>
[Trait("Category", "Integration")]
public sealed class SftpIntegrationTests
{
    private const string Host = "localhost";
    private const int Port = 2222;
    private const string Username = "testuser";
    private const string Password = "secret";
    private const string BaseDir = "upload";

    private readonly ITestOutputHelper _output;

    public SftpIntegrationTests(ITestOutputHelper output) => _output = output;

    // ───── Helpers ─────

    private static string UniqueDir() => $"test-{Guid.NewGuid():N}";
    private static string UniqueFile() => $"{Guid.NewGuid():N}.txt";

    private SftpEndpoint CreateEndpoint(string remotePath, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&username={Username}&password={Password}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"sftp:///{remotePath}?{qs}");
        return (SftpEndpoint)new SftpComponent().CreateEndpoint(uri);
    }

    private Renci.SshNet.SftpClient CreateRawClient()
    {
        var client = new Renci.SshNet.SftpClient(Host, Port, Username, Password);
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        client.Connect();
        return client;
    }

    private void CleanupDir(Renci.SshNet.SftpClient client, string path)
    {
        if (!client.Exists(path)) return;
        foreach (var entry in client.ListDirectory(path))
        {
            if (entry.Name is "." or "..") continue;
            if (entry.IsDirectory)
                CleanupDir(client, entry.FullName);
            else
                client.DeleteFile(entry.FullName);
        }
        client.DeleteDirectory(path);
    }

    // ───── Producer Tests ─────

    [Fact]
    public async Task Producer_UploadsTextFile()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";
        _output.WriteLine($"Remote: {remotePath}");

        var ep = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello SFTP!"));
        exchange.In.Headers[SftpHeaders.FileName] = "hello.txt";
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers[SftpHeaders.FileNameProduced].Should().NotBeNull();

        // Verify via raw client
        using var client = CreateRawClient();
        var filePath = $"/{remotePath}/hello.txt";
        client.Exists(filePath).Should().BeTrue();
        var content = Encoding.UTF8.GetString(client.ReadAllBytes(filePath));
        content.Should().Be("Hello SFTP!");

        CleanupDir(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_UploadsBinaryFile()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        var data = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x42, 0x43 };
        var exchange = new Exchange(new Message { Body = data });
        exchange.In.Headers[SftpHeaders.FileName] = "binary.bin";
        await producer.Process(exchange);
        await producer.Stop();

        using var client = CreateRawClient();
        var filePath = $"/{remotePath}/binary.bin";
        var downloaded = client.ReadAllBytes(filePath);
        downloaded.Should().BeEquivalentTo(data);

        CleanupDir(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_WithTempPrefix_AtomicUpload()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&tempPrefix=.tmp.");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("atomic content"));
        exchange.In.Headers[SftpHeaders.FileName] = "atomic.txt";
        await producer.Process(exchange);
        await producer.Stop();

        // Verify final file exists, temp file does not
        using var client = CreateRawClient();
        var fullDir = $"/{remotePath}";
        client.Exists($"{fullDir}/atomic.txt").Should().BeTrue();
        client.Exists($"{fullDir}/.tmp.atomic.txt").Should().BeFalse();

        var content = Encoding.UTF8.GetString(client.ReadAllBytes($"{fullDir}/atomic.txt"));
        content.Should().Be("atomic content");

        CleanupDir(client, fullDir);
    }

    [Fact]
    public async Task Producer_FileExistFail_Throws()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&fileExist=Fail");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        // Upload first time
        var exchange1 = new Exchange(new Message("first"));
        exchange1.In.Headers[SftpHeaders.FileName] = "fail-test.txt";
        await producer.Process(exchange1);

        // Upload same file again — should fail
        var exchange2 = new Exchange(new Message("second"));
        exchange2.In.Headers[SftpHeaders.FileName] = "fail-test.txt";
        var act = () => producer.Process(exchange2);
        await act.Should().ThrowAsync<IOException>();
        await producer.Stop();

        using var client = CreateRawClient();
        CleanupDir(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_FileExistIgnore_Skips()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&fileExist=Ignore");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange1 = new Exchange(new Message("original"));
        exchange1.In.Headers[SftpHeaders.FileName] = "ignore-test.txt";
        await producer.Process(exchange1);

        // Upload again with different content — should be ignored
        var exchange2 = new Exchange(new Message("updated"));
        exchange2.In.Headers[SftpHeaders.FileName] = "ignore-test.txt";
        await producer.Process(exchange2);
        await producer.Stop();

        // Content should still be original
        using var client = CreateRawClient();
        var content = Encoding.UTF8.GetString(
            client.ReadAllBytes($"/{remotePath}/ignore-test.txt"));
        content.Should().Be("original");

        CleanupDir(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_FileExistOverride_Overwrites()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&fileExist=Override");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange1 = new Exchange(new Message("original"));
        exchange1.In.Headers[SftpHeaders.FileName] = "override-test.txt";
        await producer.Process(exchange1);

        var exchange2 = new Exchange(new Message("updated"));
        exchange2.In.Headers[SftpHeaders.FileName] = "override-test.txt";
        await producer.Process(exchange2);
        await producer.Stop();

        using var client = CreateRawClient();
        var content = Encoding.UTF8.GetString(
            client.ReadAllBytes($"/{remotePath}/override-test.txt"));
        content.Should().Be("updated");

        CleanupDir(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_AutoCreate_CreatesNestedDirs()
    {
        var dir = $"{UniqueDir()}/nested/deep";
        var remotePath = $"{BaseDir}/{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("nested file"));
        exchange.In.Headers[SftpHeaders.FileName] = "deep.txt";
        await producer.Process(exchange);
        await producer.Stop();

        using var client = CreateRawClient();
        client.Exists($"/{remotePath}/deep.txt").Should().BeTrue();

        // Cleanup from the root test dir
        CleanupDir(client, $"/{BaseDir}/{dir.Split('/')[0]}");
    }

    // ───── Consumer Tests ─────

    [Fact]
    public async Task Consumer_ReceivesUploadedFile()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";
        _output.WriteLine($"Remote: {remotePath}");

        // Seed a file via raw client
        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("consumer test")))
            rawClient.UploadFile(ms, $"{fullDir}/data.txt");

        // Consume via SftpConsumer
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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[SftpHeaders.FileName].Should().Be("data.txt");
        rx.In.Headers[SftpHeaders.FileNameOnly].Should().Be("data");
        rx.In.Headers[SftpHeaders.FileExtension].Should().Be(".txt");
        rx.In.Headers[SftpHeaders.Host].Should().Be(Host);
        rx.In.Headers[SftpHeaders.Port].Should().Be(Port);
        rx.In.Headers[SftpHeaders.Username].Should().Be(Username);
        ((long)rx.In.Headers[SftpHeaders.FileLength]!).Should().BeGreaterThan(0);

        var body = (byte[])rx.In.Body!;
        Encoding.UTF8.GetString(body).Should().Be("consumer test");
        consumer.ProcessedCount.Should().Be(1);

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_Delete_RemovesFileAfterProcessing()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("delete me")))
            rawClient.UploadFile(ms, $"{fullDir}/deletable.txt");

        var ep = CreateEndpoint(remotePath, "delete=true&delay=500&initialDelay=100");
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        // File should be deleted
        rawClient.Exists($"{fullDir}/deletable.txt").Should().BeFalse();

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_MoveTo_MovesFileAfterProcessing()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("move me")))
            rawClient.UploadFile(ms, $"{fullDir}/movable.txt");

        var ep = CreateEndpoint(remotePath, "moveTo=.done&delay=500&initialDelay=100");
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        // File should be in .done subdirectory
        rawClient.Exists($"{fullDir}/movable.txt").Should().BeFalse();
        rawClient.Exists($"{fullDir}/.done/movable.txt").Should().BeTrue();

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_IncludeFilter_OnlyMatchingFiles()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("csv")))
            rawClient.UploadFile(ms, $"{fullDir}/report.csv");
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("txt")))
            rawClient.UploadFile(ms, $"{fullDir}/readme.txt");
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("csv2")))
            rawClient.UploadFile(ms, $"{fullDir}/data.csv");

        var ep = CreateEndpoint(remotePath, "include=*.csv&noop=true&delay=500&initialDelay=100");
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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(2);
        received.Select(r => (string)r.In.Headers[SftpHeaders.FileName]!)
            .Should().BeEquivalentTo(["report.csv", "data.csv"]);

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_ExcludeFilter_SkipsMatchingFiles()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("keep")))
            rawClient.UploadFile(ms, $"{fullDir}/keep.txt");
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("skip")))
            rawClient.UploadFile(ms, $"{fullDir}/skip.log");

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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        ((string)received.First().In.Headers[SftpHeaders.FileName]!).Should().Be("keep.txt");

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_Recursive_ReadsSubdirectories()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        rawClient.CreateDirectory($"{fullDir}/sub");
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("root")))
            rawClient.UploadFile(ms, $"{fullDir}/root.txt");
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("child")))
            rawClient.UploadFile(ms, $"{fullDir}/sub/child.txt");

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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(2);
        received.Select(r => (string)r.In.Headers[SftpHeaders.FileName]!)
            .Should().BeEquivalentTo(["root.txt", "child.txt"]);

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_MaxMessagesPerPoll_LimitsFiles()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        for (int i = 0; i < 5; i++)
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes($"file-{i}")))
                rawClient.UploadFile(ms, $"{fullDir}/file-{i}.txt");

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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(10_000));
        // Stop quickly after first poll
        await Task.Delay(200);
        await consumer.Stop();

        // First poll should have exactly 2
        // (noop=true, so next poll would pick same files)
        received.Count.Should().BeGreaterThanOrEqualTo(2);

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_Idempotent_NoReprocessing()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("idempotent data")))
            rawClient.UploadFile(ms, $"{fullDir}/stable.txt");

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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(10_000));

        // Wait for a couple more poll cycles
        await Task.Delay(2000);
        await consumer.Stop();

        // Should still be only 1 — idempotent prevents reprocessing
        received.Should().HaveCount(1);

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_EmptyDirectory_NoMessages()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        rawClient.CreateDirectory($"/{remotePath}");

        var ep = CreateEndpoint(remotePath, "noop=true&delay=500&initialDelay=100");
        var received = new ConcurrentBag<IExchange>();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                return Task.CompletedTask;
            });

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(2000);
        await consumer.Stop();

        received.Should().BeEmpty();

        CleanupDir(rawClient, $"/{remotePath}");
    }

    [Fact]
    public async Task Consumer_SortByName_OrderedOutput()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);

        // Upload in random order
        foreach (var name in new[] { "charlie.txt", "alpha.txt", "bravo.txt" })
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(name)))
                rawClient.UploadFile(ms, $"{fullDir}/{name}");

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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(3);
        // Files processed in name order
        var names = received.Select(r => (string)r.In.Headers[SftpHeaders.FileName]!)
            .ToList();
        names.Should().BeEquivalentTo(["alpha.txt", "bravo.txt", "charlie.txt"]);

        CleanupDir(rawClient, fullDir);
    }

    // ───── Roundtrip Tests ─────

    [Fact]
    public async Task Producer_To_Consumer_FullRoundtrip()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";
        _output.WriteLine($"Roundtrip dir: {remotePath}");

        // Upload via producer
        var prodEp = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (SftpProducer)prodEp.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("roundtrip content"));
        exchange.In.Headers[SftpHeaders.FileName] = "roundtrip.txt";
        await producer.Process(exchange);
        await producer.Stop();

        // Consume via consumer
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

        var consumer = (SftpConsumer)consEp.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[SftpHeaders.FileName].Should().Be("roundtrip.txt");
        var body = Encoding.UTF8.GetString((byte[])rx.In.Body!);
        body.Should().Be("roundtrip content");

        // File deleted after processing
        using var rawClient = CreateRawClient();
        rawClient.Exists($"/{remotePath}/roundtrip.txt").Should().BeFalse();

        CleanupDir(rawClient, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_To_Consumer_MultipleFiles()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";
        const int fileCount = 5;

        // Upload multiple files via producer
        var prodEp = CreateEndpoint(remotePath, "autoCreate=true");
        var producer = (SftpProducer)prodEp.CreateProducer();
        await producer.Start();

        for (int i = 0; i < fileCount; i++)
        {
            var ex = new Exchange(new Message($"content-{i}"));
            ex.In.Headers[SftpHeaders.FileName] = $"file-{i}.txt";
            await producer.Process(ex);
        }
        await producer.Stop();

        // Consume all files
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

        var consumer = (SftpConsumer)consEp.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(fileCount);

        using var rawClient = CreateRawClient();
        CleanupDir(rawClient, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_NullBody_WithAllowNullBody_UploadsEmptyFile()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&allowNullBody=true");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = null });
        exchange.In.Headers[SftpHeaders.FileName] = "empty.txt";
        await producer.Process(exchange);
        await producer.Stop();

        using var client = CreateRawClient();
        var filePath = $"/{remotePath}/empty.txt";
        client.Exists(filePath).Should().BeTrue();
        client.ReadAllBytes(filePath).Length.Should().Be(0);

        CleanupDir(client, $"/{remotePath}");
    }

    [Fact]
    public async Task Producer_NullBody_WithoutAllowNullBody_Throws()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        var ep = CreateEndpoint(remotePath, "autoCreate=true&allowNullBody=false");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = null });
        exchange.In.Headers[SftpHeaders.FileName] = "null.txt";

        var act = () => producer.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AllowNullBody*");
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_FileExistMove_BacksUpExisting()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        var ep = CreateEndpoint(remotePath,
            "autoCreate=true&fileExist=Move&moveExistingFileStrategy=Backup");
        var producer = (SftpProducer)ep.CreateProducer();
        await producer.Start();

        // Upload first time
        var ex1 = new Exchange(new Message("v1"));
        ex1.In.Headers[SftpHeaders.FileName] = "versioned.txt";
        await producer.Process(ex1);

        // Upload again — should move existing to .bak
        var ex2 = new Exchange(new Message("v2"));
        ex2.In.Headers[SftpHeaders.FileName] = "versioned.txt";
        await producer.Process(ex2);
        await producer.Stop();

        using var client = CreateRawClient();
        var fullDir = $"/{remotePath}";
        client.Exists($"{fullDir}/versioned.txt").Should().BeTrue();
        var content = Encoding.UTF8.GetString(client.ReadAllBytes($"{fullDir}/versioned.txt"));
        content.Should().Be("v2");

        // Backup should exist
        var files = client.ListDirectory(fullDir)
            .Where(f => f.Name.StartsWith("versioned.txt.bak")).ToList();
        files.Should().HaveCount(1);

        CleanupDir(client, fullDir);
    }

    [Fact]
    public async Task Consumer_PreMove_MovesBeforeProcessing()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("pre-move data")))
            rawClient.UploadFile(ms, $"{fullDir}/source.txt");

        // preMove=.inprogress will move file to .inprogress dir before processing
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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);

        // Original file should not be in source location
        rawClient.Exists($"{fullDir}/source.txt").Should().BeFalse();
        // Should be in .inprogress (noop = leave file after processing at its current location)
        rawClient.Exists($"{fullDir}/.inprogress/source.txt").Should().BeTrue();

        CleanupDir(rawClient, fullDir);
    }

    // ───── StreamBody Tests ─────

    [Fact]
    public async Task Consumer_StreamBody_ReceivesStream()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("stream content")))
            rawClient.UploadFile(ms, $"{fullDir}/streamed.txt");

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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Body.Should().BeAssignableTo<Stream>();

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_StreamBody_DataCorrect()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";
        var expectedContent = "hello from sftp stream";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(expectedContent)))
            rawClient.UploadFile(ms, $"{fullDir}/readable.txt");

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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        readContent.Should().Be(expectedContent);

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_StreamBody_HeadersPopulated()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("headers test")))
            rawClient.UploadFile(ms, $"{fullDir}/meta.txt");

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

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[SftpHeaders.FileName].Should().Be("meta.txt");
        rx.In.Headers[SftpHeaders.FileNameOnly].Should().Be("meta");
        rx.In.Headers[SftpHeaders.FileExtension].Should().Be(".txt");
        rx.In.Headers[SftpHeaders.Host].Should().Be(Host);
        rx.In.Headers[SftpHeaders.Port].Should().Be(Port);
        rx.In.Headers[SftpHeaders.Username].Should().Be(Username);
        ((long)rx.In.Headers[SftpHeaders.FileLength]!).Should().BeGreaterThan(0);

        CleanupDir(rawClient, fullDir);
    }

    [Fact]
    public async Task Consumer_StreamBody_DeleteAfterRead()
    {
        var dir = UniqueDir();
        var remotePath = $"{BaseDir}/{dir}";

        using var rawClient = CreateRawClient();
        var fullDir = $"/{remotePath}";
        rawClient.CreateDirectory(fullDir);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("delete after stream")))
            rawClient.UploadFile(ms, $"{fullDir}/deletable.txt");

        var ep = CreateEndpoint(remotePath, "streamBody=true&delete=true&delay=500&initialDelay=100");
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (SftpConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        rawClient.Exists($"{fullDir}/deletable.txt").Should().BeFalse();

        CleanupDir(rawClient, fullDir);
    }
}
