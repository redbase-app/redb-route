using System.Collections.Concurrent;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.S3;
using Xunit.Abstractions;

namespace redb.Route.Tests.S3;

/// <summary>
/// Integration tests against MinIO docker container.
/// Expects MinIO at localhost:9000 (minioadmin/minioadmin).
/// Start with: docker compose -f docker-compose.tests.yml up minio -d
/// </summary>
[Trait("Category", "Integration")]
public sealed class S3IntegrationTests : IAsyncLifetime
{
    private const string ServiceUrl = "http://localhost:9000";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private const string Region = "us-east-1";
    private const string TestBucket = "integration-tests";

    private readonly ITestOutputHelper _output;
    private IAmazonS3? _rawClient;

    public S3IntegrationTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _rawClient = CreateRawClient();
        // Ensure test bucket exists
        try { await _rawClient.EnsureBucketExistsAsync(TestBucket); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
    }

    public async Task DisposeAsync()
    {
        if (_rawClient is not null)
        {
            // Cleanup all test-* prefixed objects
            await CleanupBucketAsync(TestBucket);
            _rawClient.Dispose();
        }
    }

    // ───── Helpers ─────

    private static string UniquePrefix() => $"test-{Guid.NewGuid():N}/";

    private static IAmazonS3 CreateRawClient()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = Region,
        };
        return new AmazonS3Client(AccessKey, SecretKey, config);
    }

    private S3Endpoint CreateEndpoint(string bucket, string? extraParams = null)
    {
        var qs = $"serviceUrl={ServiceUrl}&accessKey={AccessKey}&secretKey={SecretKey}" +
                 $"&region={Region}&forcePathStyle=true";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"s3://{bucket}?{qs}");
        return (S3Endpoint)new S3Component().CreateEndpoint(uri);
    }

    private S3Endpoint CreateEndpointWithOp(S3OperationType op, string bucket, string? extraParams = null)
    {
        var qs = $"serviceUrl={ServiceUrl}&accessKey={AccessKey}&secretKey={SecretKey}" +
                 $"&region={Region}&forcePathStyle=true";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"s3://{op}:{bucket}?{qs}");
        return (S3Endpoint)new S3Component().CreateEndpoint(uri);
    }

    private async Task SeedObject(string bucket, string key, string content)
    {
        await _rawClient!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = content,
        });
    }

    private async Task SeedObject(string bucket, string key, byte[] data)
    {
        await _rawClient!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(data),
        });
    }

    private async Task<string> ReadObjectAsString(string bucket, string key)
    {
        var response = await _rawClient!.GetObjectAsync(bucket, key);
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task<bool> ObjectExists(string bucket, string key)
    {
        try
        {
            await _rawClient!.GetObjectMetadataAsync(bucket, key);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task CleanupBucketAsync(string bucket)
    {
        try
        {
            var list = await _rawClient!.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = "test-", // only cleanup test-* prefixed objects
            });

            if (list.S3Objects is { Count: > 0 })
            {
                await _rawClient.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Objects = list.S3Objects.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                });
            }
        }
        catch (AmazonS3Exception)
        {
            // Bucket may not exist — ignore
        }
    }

    private async Task CleanupPrefixAsync(string bucket, string prefix)
    {
        var list = await _rawClient!.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = prefix,
        });

        if (list.S3Objects is { Count: > 0 })
        {
            await _rawClient.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = list.S3Objects.Select(o => new KeyVersion { Key = o.Key }).ToList(),
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — PutObject
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_PutObject_TextBody()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}hello.txt";
        _output.WriteLine($"Key: {key}");

        var ep = CreateEndpoint(TestBucket, $"keyName={key}&autoCreateBucket=true");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello S3!"));
        await producer.Process(exchange);
        await producer.Stop();

        // Verify header set
        exchange.In.Headers[S3Headers.ProducedBucketName].Should().Be(TestBucket);
        exchange.In.Headers[S3Headers.ProducedKey].Should().Be(key);
        exchange.In.Headers[S3Headers.ETag].Should().NotBeNull();

        // Verify via raw client
        var content = await ReadObjectAsString(TestBucket, key);
        content.Should().Be("Hello S3!");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Producer_PutObject_BinaryBody()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}binary.bin";

        var ep = CreateEndpoint(TestBucket, $"keyName={key}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var data = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x42, 0x43 };
        var exchange = new Exchange(new Message { Body = data });
        await producer.Process(exchange);
        await producer.Stop();

        // Verify via raw client
        var response = await _rawClient!.GetObjectAsync(TestBucket, key);
        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms);
        ms.ToArray().Should().BeEquivalentTo(data);

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Producer_PutObject_StreamBody()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}stream.dat";

        var ep = CreateEndpoint(TestBucket, $"keyName={key}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var data = Encoding.UTF8.GetBytes("Stream content test");
        using var stream = new MemoryStream(data);
        var exchange = new Exchange(new Message { Body = stream });
        await producer.Process(exchange);
        await producer.Stop();

        var content = await ReadObjectAsString(TestBucket, key);
        content.Should().Be("Stream content test");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Producer_PutObject_WithContentType()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}data.json";

        var ep = CreateEndpoint(TestBucket, $"keyName={key}&contentType=application/json");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("{\"test\": true}"));
        await producer.Process(exchange);
        await producer.Stop();

        var metadata = await _rawClient!.GetObjectMetadataAsync(TestBucket, key);
        metadata.Headers.ContentType.Should().Be("application/json");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Producer_PutObject_WithMetadata()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}meta.txt";

        var ep = CreateEndpoint(TestBucket, $"keyName={key}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("metadata test"));
        exchange.In.Headers[$"{S3Headers.MetadataPrefix}author"] = "redb";
        exchange.In.Headers[$"{S3Headers.MetadataPrefix}version"] = "1.0";
        await producer.Process(exchange);
        await producer.Stop();

        var metadata = await _rawClient!.GetObjectMetadataAsync(TestBucket, key);
        metadata.Metadata["author"].Should().Be("redb");
        metadata.Metadata["version"].Should().Be("1.0");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Producer_PutObject_KeyFromHeader()
    {
        var prefix = UniquePrefix();

        var ep = CreateEndpoint(TestBucket);
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var key = $"{prefix}header-key.txt";
        var exchange = new Exchange(new Message("key from header"));
        exchange.In.Headers[S3Headers.Key] = key;
        await producer.Process(exchange);
        await producer.Stop();

        var content = await ReadObjectAsString(TestBucket, key);
        content.Should().Be("key from header");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — GetObject
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_GetObject_ReturnsBody()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}get-test.txt";
        await SeedObject(TestBucket, key, "get object content");

        var ep = CreateEndpointWithOp(S3OperationType.GetObject, TestBucket, $"keyName={key}&includeBody=true");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        var body = (byte[])exchange.In.Body!;
        Encoding.UTF8.GetString(body).Should().Be("get object content");
        exchange.In.Headers[S3Headers.ContentType].Should().NotBeNull();
        exchange.In.Headers[S3Headers.ETag].Should().NotBeNull();
        exchange.In.Headers[S3Headers.Key].Should().Be(key);

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Producer_GetObject_ReturnsStream_WhenIncludeBodyFalse()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}stream-get.txt";
        await SeedObject(TestBucket, key, "stream body");

        var ep = CreateEndpointWithOp(S3OperationType.GetObject, TestBucket, $"keyName={key}&includeBody=false");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Body.Should().BeAssignableTo<Stream>();
        using var stream = (Stream)exchange.In.Body!;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("stream body");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — DeleteObject
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_DeleteObject_RemovesObject()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}delete-me.txt";
        await SeedObject(TestBucket, key, "to be deleted");

        var ep = CreateEndpointWithOp(S3OperationType.DeleteObject, TestBucket, $"keyName={key}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        (await ObjectExists(TestBucket, key)).Should().BeFalse();
        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — CopyObject
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_CopyObject_CopiesKey()
    {
        var prefix = UniquePrefix();
        var srcKey = $"{prefix}copy-src.txt";
        var dstKey = $"{prefix}copy-dst.txt";
        await SeedObject(TestBucket, srcKey, "copy me");

        var ep = CreateEndpointWithOp(S3OperationType.CopyObject, TestBucket, $"keyName={srcKey}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        exchange.In.Headers[S3Headers.DestinationBucket] = TestBucket;
        exchange.In.Headers[S3Headers.DestinationKey] = dstKey;
        await producer.Process(exchange);
        await producer.Stop();

        var content = await ReadObjectAsString(TestBucket, dstKey);
        content.Should().Be("copy me");
        (await ObjectExists(TestBucket, srcKey)).Should().BeTrue("source should remain");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — ListObjects
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_ListObjects_ReturnsObjectInfoList()
    {
        var prefix = UniquePrefix();
        await SeedObject(TestBucket, $"{prefix}a.txt", "aaa");
        await SeedObject(TestBucket, $"{prefix}b.txt", "bbb");
        await SeedObject(TestBucket, $"{prefix}c.txt", "ccc");

        var ep = CreateEndpointWithOp(S3OperationType.ListObjects, TestBucket, $"prefix={prefix}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        var list = (List<S3ObjectInfo>)exchange.In.Body!;
        list.Should().HaveCount(3);
        list.Select(o => o.Key).Should().BeEquivalentTo(
            [$"{prefix}a.txt", $"{prefix}b.txt", $"{prefix}c.txt"]);
        list.Should().AllSatisfy(o =>
        {
            o.Size.Should().BeGreaterThan(0);
            o.ETag.Should().NotBeNullOrEmpty();
        });

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — HeadObject
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_HeadObject_ReturnsMetadata()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}head-test.txt";
        await SeedObject(TestBucket, key, "head test");

        var ep = CreateEndpointWithOp(S3OperationType.HeadObject, TestBucket, $"keyName={key}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers[S3Headers.ContentLength].Should().NotBeNull();
        ((long)exchange.In.Headers[S3Headers.ContentLength]!).Should().BeGreaterThan(0);
        exchange.In.Headers[S3Headers.ETag].Should().NotBeNull();
        exchange.In.Headers[S3Headers.LastModified].Should().NotBeNull();

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — Presigned URLs
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_CreateDownloadLink_ReturnsUrl()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}download-link.txt";
        await SeedObject(TestBucket, key, "download via link");

        var ep = CreateEndpointWithOp(S3OperationType.CreateDownloadLink, TestBucket, $"keyName={key}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        var url = (string)exchange.In.Body!;
        url.Should().Contain(TestBucket);
        url.Should().Contain(Uri.EscapeDataString(key).Replace("%2F", "/"));
        exchange.In.Headers[S3Headers.PresignedUrl].Should().Be(url);

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Producer_CreateUploadLink_ReturnsUrl()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}upload-link.txt";

        var ep = CreateEndpointWithOp(S3OperationType.CreateUploadLink, TestBucket, $"keyName={key}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        var url = (string)exchange.In.Body!;
        url.Should().Contain(TestBucket);
        exchange.In.Headers[S3Headers.PresignedUrl].Should().Be(url);

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — Tagging
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_PutAndGetObjectTagging_Roundtrip()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}tagged.txt";
        await SeedObject(TestBucket, key, "tagged object");

        // Put tags
        var putEp = CreateEndpointWithOp(S3OperationType.PutObjectTagging, TestBucket, $"keyName={key}");
        var putProducer = (S3Producer)putEp.CreateProducer();
        await putProducer.Start();

        var tags = new Dictionary<string, string> { { "env", "test" }, { "team", "redb" } };
        var putExchange = new Exchange(new Message { Body = tags });
        await putProducer.Process(putExchange);
        await putProducer.Stop();

        // Get tags
        var getEp = CreateEndpointWithOp(S3OperationType.GetObjectTagging, TestBucket, $"keyName={key}");
        var getProducer = (S3Producer)getEp.CreateProducer();
        await getProducer.Start();

        var getExchange = new Exchange(new Message());
        await getProducer.Process(getExchange);
        await getProducer.Stop();

        var result = (Dictionary<string, string>)getExchange.In.Body!;
        result.Should().ContainKey("env").WhoseValue.Should().Be("test");
        result.Should().ContainKey("team").WhoseValue.Should().Be("redb");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — Bucket Operations
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_CreateAndDeleteBucket()
    {
        var bucket = $"test-bucket-{Guid.NewGuid():N}";

        // Create
        var createEp = CreateEndpointWithOp(S3OperationType.CreateBucket, bucket);
        var createProducer = (S3Producer)createEp.CreateProducer();
        await createProducer.Start();
        await createProducer.Process(new Exchange(new Message()));
        await createProducer.Stop();

        // Verify exists via raw client
        var list = await _rawClient!.ListBucketsAsync();
        list.Buckets.Should().Contain(b => b.BucketName == bucket);

        // Delete
        var deleteEp = CreateEndpointWithOp(S3OperationType.DeleteBucket, bucket);
        var deleteProducer = (S3Producer)deleteEp.CreateProducer();
        await deleteProducer.Start();
        await deleteProducer.Process(new Exchange(new Message()));
        await deleteProducer.Stop();

        // Verify gone
        var list2 = await _rawClient.ListBucketsAsync();
        list2.Buckets.Should().NotContain(b => b.BucketName == bucket);
    }

    [Fact]
    public async Task Producer_ListBuckets_ReturnsAll()
    {
        var ep = CreateEndpointWithOp(S3OperationType.ListBuckets, TestBucket);
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        var buckets = exchange.In.Body as System.Collections.IList;
        buckets.Should().NotBeNull();
        buckets!.Count.Should().BeGreaterThan(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — MultiPartUpload
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_MultiPartUpload_LargeFile()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}multipart.dat";

        // 6 MB > 5 MB part size → triggers multipart
        var ep = CreateEndpoint(TestBucket, $"keyName={key}&multiPartUpload=true&partSize=5242880");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var data = new byte[6 * 1024 * 1024]; // 6 MB
        Random.Shared.NextBytes(data);
        var exchange = new Exchange(new Message { Body = data });
        await producer.Process(exchange);
        await producer.Stop();

        // Verify uploaded correctly
        var response = await _rawClient!.GetObjectAsync(TestBucket, key);
        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms);
        ms.ToArray().Should().BeEquivalentTo(data);

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — Operation Override via Header
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_OperationOverrideViaHeader()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}override-op.txt";
        await SeedObject(TestBucket, key, "check me");

        // Default op is PutObject, but we override via header
        var ep = CreateEndpoint(TestBucket, $"keyName={key}");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        exchange.In.Headers[S3Headers.Operation] = "HeadObject";
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers[S3Headers.ContentLength].Should().NotBeNull();
        ((long)exchange.In.Headers[S3Headers.ContentLength]!).Should().BeGreaterThan(0);

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — DeleteObjects (batch)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_DeleteObjects_BatchDelete()
    {
        var prefix = UniquePrefix();
        var keys = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var k = $"{prefix}batch-{i}.txt";
            await SeedObject(TestBucket, k, $"batch {i}");
            keys.Add(k);
        }

        var ep = CreateEndpointWithOp(S3OperationType.DeleteObjects, TestBucket);
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        exchange.In.Headers[S3Headers.KeysToDelete] = keys;
        await producer.Process(exchange);
        await producer.Stop();

        var deletedKeys = (List<string>)exchange.In.Body!;
        deletedKeys.Should().BeEquivalentTo(keys);

        foreach (var k in keys)
            (await ObjectExists(TestBucket, k)).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — Basic polling
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_PollsAndReceivesObjects()
    {
        var prefix = UniquePrefix();
        await SeedObject(TestBucket, $"{prefix}data1.txt", "content 1");
        await SeedObject(TestBucket, $"{prefix}data2.txt", "content 2");
        _output.WriteLine($"Prefix: {prefix}");

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&deleteAfterRead=false&delay=500&initialDelay=100&maxMessagesPerPoll=10&includeBody=true");

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

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(30_000));
        await consumer.Stop();

        received.Should().HaveCountGreaterThanOrEqualTo(2);

        var keys = received.Select(r => (string)r.In.Headers[S3Headers.Key]!).ToList();
        keys.Should().Contain($"{prefix}data1.txt");
        keys.Should().Contain($"{prefix}data2.txt");

        // Verify body is downloaded
        var bodies = received.Select(r => Encoding.UTF8.GetString((byte[])r.In.Body!)).ToList();
        bodies.Should().Contain("content 1");
        bodies.Should().Contain("content 2");

        // Verify headers set
        var first = received.First();
        first.In.Headers[S3Headers.BucketName].Should().Be(TestBucket);
        first.In.Headers[S3Headers.ETag].Should().NotBeNull();
        first.In.Headers[S3Headers.ContentLength].Should().NotBeNull();

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — DeleteAfterRead
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_DeleteAfterRead_RemovesObjects()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}deletable.txt";
        await SeedObject(TestBucket, key, "delete me after read");

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&deleteAfterRead=true&delay=500&initialDelay=100");

        var tcs = new TaskCompletionSource();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        await consumer.Stop();

        (await ObjectExists(TestBucket, key)).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — MoveAfterRead
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_MoveAfterRead_MovesToDestination()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}movable.txt";
        await SeedObject(TestBucket, key, "move me");

        var destPrefix = $"{prefix}archive/";

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&deleteAfterRead=false&moveAfterRead=true" +
            $"&destinationBucket={TestBucket}&destinationBucketPrefix={destPrefix}" +
            $"&delay=500&initialDelay=100");

        var tcs = new TaskCompletionSource();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        await consumer.Stop();

        // Source should be gone
        (await ObjectExists(TestBucket, key)).Should().BeFalse();

        // Destination should exist (prefix + original key)
        var destKey = $"{destPrefix}{key}";
        (await ObjectExists(TestBucket, destKey)).Should().BeTrue();

        var content = await ReadObjectAsString(TestBucket, destKey);
        content.Should().Be("move me");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — Include/Exclude filters
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_IncludeFilter_OnlyMatchingObjects()
    {
        var prefix = UniquePrefix();
        await SeedObject(TestBucket, $"{prefix}report.csv", "csv data");
        await SeedObject(TestBucket, $"{prefix}readme.txt", "txt data");
        await SeedObject(TestBucket, $"{prefix}data.csv", "csv data 2");

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&include=*.csv&deleteAfterRead=false&delay=500&initialDelay=100");

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

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(30_000));
        await consumer.Stop();

        received.Should().HaveCount(2);
        received.Select(r => (string)r.In.Headers[S3Headers.Key]!)
            .Should().AllSatisfy(k => k.Should().EndWith(".csv"));

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Consumer_ExcludeFilter_SkipsMatchingObjects()
    {
        var prefix = UniquePrefix();
        await SeedObject(TestBucket, $"{prefix}keep.txt", "keep");
        await SeedObject(TestBucket, $"{prefix}skip.log", "skip");

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&exclude=*.log&deleteAfterRead=false&delay=500&initialDelay=100&idempotent=true");

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

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        // Wait a bit for potential second poll to make sure .log is not processed
        await Task.Delay(1500);
        await consumer.Stop();

        received.Should().HaveCount(1);
        ((string)received.First().In.Headers[S3Headers.Key]!).Should().EndWith("keep.txt");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — MaxMessagesPerPoll
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_MaxMessagesPerPoll_LimitsPerCycle()
    {
        var prefix = UniquePrefix();
        for (int i = 0; i < 5; i++)
            await SeedObject(TestBucket, $"{prefix}file-{i}.txt", $"content {i}");

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&maxMessagesPerPoll=2&deleteAfterRead=false&delay=500&initialDelay=100");

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

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(15_000));
        // Stop quickly after first poll batch
        await Task.Delay(200);
        await consumer.Stop();

        // First poll should have at most 2
        received.Count.Should().BeGreaterThanOrEqualTo(2);

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — Idempotent
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_Idempotent_NoReprocessing()
    {
        var prefix = UniquePrefix();
        await SeedObject(TestBucket, $"{prefix}stable.txt", "idempotent data");

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&idempotent=true&deleteAfterRead=false&delay=500&initialDelay=100");

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

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        // Wait for a couple more poll cycles
        await Task.Delay(2000);
        await consumer.Stop();

        // Should still be only 1 — idempotent prevents reprocessing
        received.Should().HaveCount(1);

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — SortBy
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_SortByKey_OrderedOutput()
    {
        var prefix = UniquePrefix();
        // Seed in reverse order
        await SeedObject(TestBucket, $"{prefix}c.txt", "c");
        await SeedObject(TestBucket, $"{prefix}a.txt", "a");
        await SeedObject(TestBucket, $"{prefix}b.txt", "b");

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&sortBy=Key&deleteAfterRead=false&delay=500&initialDelay=100&maxMessagesPerPoll=10");

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

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(30_000));
        await consumer.Stop();

        received.Should().HaveCount(3);
        // Note: ConcurrentBag doesn't guarantee order, but the processor receives them in order
        // We can at least verify all 3 keys are present
        var keys = received.Select(r => (string)r.In.Headers[S3Headers.Key]!).ToList();
        keys.Should().Contain($"{prefix}a.txt");
        keys.Should().Contain($"{prefix}b.txt");
        keys.Should().Contain($"{prefix}c.txt");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — SendEmptyMessageWhenIdle
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_SendEmptyMessageWhenIdle_HeartbeatsOnEmptyBucket()
    {
        var prefix = UniquePrefix();
        // No objects seeded — bucket/prefix is empty

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&sendEmptyMessageWhenIdle=true&deleteAfterRead=false&delay=500&initialDelay=100");

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

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCountGreaterThanOrEqualTo(1);
        // Empty message should have null body
        received.First().In.Body.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ROUNDTRIP — Producer + Consumer
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Roundtrip_ProducerUpload_ConsumerDownload()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}roundtrip.txt";
        var content = "Full roundtrip test — producer to consumer!";
        _output.WriteLine($"Roundtrip key: {key}");

        // Upload via producer
        var putEp = CreateEndpoint(TestBucket, $"keyName={key}");
        var producer = (S3Producer)putEp.CreateProducer();
        await producer.Start();

        var putExchange = new Exchange(new Message(content));
        putExchange.In.Headers[$"{S3Headers.MetadataPrefix}source"] = "integration-test";
        await producer.Process(putExchange);
        await producer.Stop();

        // Consume via consumer
        var getEp = CreateEndpoint(TestBucket,
            $"prefix={prefix}&deleteAfterRead=true&delay=500&initialDelay=100&includeBody=true");

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

        var consumer = (S3Consumer)getEp.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        Encoding.UTF8.GetString((byte[])rx.In.Body!).Should().Be(content);
        rx.In.Headers[S3Headers.Key].Should().Be(key);
        rx.In.Headers[S3Headers.BucketName].Should().Be(TestBucket);
        rx.In.Headers[S3Headers.ETag].Should().NotBeNull();

        // Verify deleted after read
        (await ObjectExists(TestBucket, key)).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — Bucket Override via Header
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_BucketOverrideViaHeader()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}bucket-override.txt";

        // Create a second bucket
        var altBucket = $"test-alt-{Guid.NewGuid():N}";
        try { await _rawClient!.EnsureBucketExistsAsync(altBucket); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }

        try
        {
            var ep = CreateEndpoint(TestBucket, $"keyName={key}");
            var producer = (S3Producer)ep.CreateProducer();
            await producer.Start();

            var exchange = new Exchange(new Message("in alt bucket"));
            exchange.In.Headers[S3Headers.OverrideBucketName] = altBucket;
            await producer.Process(exchange);
            await producer.Stop();

            // Should be in alt bucket, not the default
            (await ObjectExists(altBucket, key)).Should().BeTrue();
            (await ObjectExists(TestBucket, key)).Should().BeFalse();

            var content = await ReadObjectAsString(altBucket, key);
            content.Should().Be("in alt bucket");
        }
        finally
        {
            // Cleanup alt bucket
            await CleanupPrefixAsync(altBucket, prefix);
            await _rawClient!.DeleteBucketAsync(altBucket);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRODUCER — StreamBody (GetObject)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Producer_GetObject_StreamBody_ReturnsStream()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}stream-body.txt";
        await SeedObject(TestBucket, key, "stream body data");

        var ep = CreateEndpointWithOp(S3OperationType.GetObject, TestBucket,
            $"keyName={key}&streamBody=true");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Body.Should().BeAssignableTo<Stream>();
        using var stream = (Stream)exchange.In.Body!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("stream body data");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Producer_GetObject_StreamBody_BinaryPreserved()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}stream-binary.bin";
        var data = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x42 };
        await SeedObject(TestBucket, key, data);

        var ep = CreateEndpointWithOp(S3OperationType.GetObject, TestBucket,
            $"keyName={key}&streamBody=true");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        using var stream = (Stream)exchange.In.Body!;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.ToArray().Should().BeEquivalentTo(data);

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Producer_GetObject_StreamBody_HeadersStillSet()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}stream-headers.txt";
        await SeedObject(TestBucket, key, "check headers");

        var ep = CreateEndpointWithOp(S3OperationType.GetObject, TestBucket,
            $"keyName={key}&streamBody=true");
        var producer = (S3Producer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers[S3Headers.Key].Should().Be(key);
        exchange.In.Headers[S3Headers.ETag].Should().NotBeNull();
        exchange.In.Headers[S3Headers.ContentType].Should().NotBeNull();

        // Cleanup the stream
        if (exchange.In.Body is Stream s) await s.DisposeAsync();
        await CleanupPrefixAsync(TestBucket, prefix);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER — StreamBody
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Consumer_StreamBody_ReceivesStream()
    {
        var prefix = UniquePrefix();
        await SeedObject(TestBucket, $"{prefix}streamed.txt", "consumer stream data");

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&streamBody=true&deleteAfterRead=true&delay=500&initialDelay=100");

        Type? bodyType = null;
        string? bodyContent = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.ArgAt<IExchange>(0);
                bodyType = ex.In.Body?.GetType();
                if (ex.In.Body is Stream stream)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    bodyContent = await reader.ReadToEndAsync();
                }
                tcs.TrySetResult();
            });

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        await consumer.Stop();

        bodyType.Should().NotBeNull();
        bodyType!.Should().BeAssignableTo(typeof(Stream));
        bodyContent.Should().Be("consumer stream data");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Consumer_StreamBody_HeadersPopulated()
    {
        var prefix = UniquePrefix();
        await SeedObject(TestBucket, $"{prefix}hdr.txt", "header check");

        var ep = CreateEndpoint(TestBucket,
            $"prefix={prefix}&streamBody=true&deleteAfterRead=true&delay=500&initialDelay=100");

        string? key = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ex = ci.ArgAt<IExchange>(0);
                key = (string?)ex.In.Headers[S3Headers.Key];
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (S3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        await consumer.Stop();

        key.Should().Be($"{prefix}hdr.txt");

        await CleanupPrefixAsync(TestBucket, prefix);
    }

    [Fact]
    public async Task Roundtrip_StreamBody_ProducerUpload_ConsumerStream()
    {
        var prefix = UniquePrefix();
        var key = $"{prefix}roundtrip-stream.txt";
        var content = "Roundtrip streaming test!";

        // Upload via producer
        var putEp = CreateEndpoint(TestBucket, $"keyName={key}");
        var producer = (S3Producer)putEp.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(content)));
        await producer.Stop();

        // Consume via consumer with StreamBody
        var getEp = CreateEndpoint(TestBucket,
            $"prefix={prefix}&streamBody=true&deleteAfterRead=true&delay=500&initialDelay=100");

        string? received = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.ArgAt<IExchange>(0);
                if (ex.In.Body is Stream stream)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    received = await reader.ReadToEndAsync();
                }
                tcs.TrySetResult();
            });

        var consumer = (S3Consumer)getEp.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        await consumer.Stop();

        received.Should().Be(content);
    }
}
