using System.Text;
using Google.Cloud.Storage.V1;
using redb.Route.Core;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// Ф11 волна Д (Д1–Д3): new Firebase Storage producer operations against fake-gcs —
/// CopyObject, signed download links, bucket operations. Parity with the S3 sibling
/// and camel-google-storage.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class StorageOperationsTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string GcsEndpoint = "http://localhost:4443/storage/v1/";
    private static readonly string TestBucket = $"storage-ops-net{Environment.Version.Major}";

    private readonly ITestOutputHelper _output;
    private StorageClient? _rawGcs;
    private FirebaseCredentialProvider? _prodProvider;
    private string? _prevEmuVar;

    public StorageOperationsTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _prevEmuVar = Environment.GetEnvironmentVariable("STORAGE_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", GcsEndpoint);
        _rawGcs = new StorageClientBuilder { BaseUri = GcsEndpoint, UnauthenticatedAccess = true }.Build();
        _prodProvider = new FirebaseCredentialProvider();
        try
        {
            await _rawGcs.CreateBucketAsync(ProjectId, TestBucket);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Conflict)
        {
        }
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", _prevEmuVar);
        _prodProvider?.Dispose();
        _rawGcs?.Dispose();
        return Task.CompletedTask;
    }

    // ═══ Д1: CopyObject ═══

    [Fact]
    public async Task Copy_SameBucket_SourceIntactDestinationCreated()
    {
        var src = $"copy-src-{Guid.NewGuid():N}.txt";
        var dest = $"copy-dest-{Guid.NewGuid():N}.txt";
        await SeedAsync(src, "copy-me");

        var producer = CreateProducer(
            $"operation=Copy&objectName={src}&destinationObjectName={dest}");
        await producer.Start();
        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        (await ReadAsync(dest)).Should().Be("copy-me");
        (await ReadAsync(src)).Should().Be("copy-me", "copy не трогает источник");
        exchange.In.Headers[FirebaseStorageHeaders.ObjectName].Should().Be(dest);
    }

    [Fact]
    public async Task Copy_HeaderOverridesForSourceAndDestination()
    {
        var src = $"copy-hsrc-{Guid.NewGuid():N}.txt";
        var dest = $"copy-hdest-{Guid.NewGuid():N}.txt";
        await SeedAsync(src, "via-headers");

        var producer = CreateProducer("operation=Copy");
        await producer.Start();
        var exchange = new Exchange(new Message());
        exchange.In.Headers[FirebaseStorageHeaders.ObjectName] = src;
        exchange.In.Headers[FirebaseStorageHeaders.DestinationObjectName] = dest;
        await producer.Process(exchange);
        await producer.Stop();

        (await ReadAsync(dest)).Should().Be("via-headers");
    }

    // ═══ Д2: CreateDownloadLink (signed URL) ═══

    [Fact]
    public async Task CreateDownloadLink_ProducesWellFormedSignedUrl()
    {
        var name = $"signed-{Guid.NewGuid():N}.txt";
        await SeedAsync(name, "signed-content");
        var sa = WriteFakeServiceAccount();

        var producer = CreateProducer(
            $"operation=CreateDownloadLink&objectName={name}" +
            $"&credentialPath={Uri.EscapeDataString(sa)}&signedUrlExpiration=60000");
        await producer.Start();
        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        var url = exchange.Out!.Body as string;
        url.Should().NotBeNullOrEmpty();
        url.Should().Contain(TestBucket).And.Contain(name);
        url.Should().Contain("X-Goog-Signature", "подписанный URL обязан нести подпись");
        exchange.Out.Headers[FirebaseStorageHeaders.DownloadUrl].Should().Be(url);
        _output.WriteLine($"Signed URL: {url![..Math.Min(120, url.Length)]}...");
    }

    [Fact]
    public async Task CreateDownloadLink_WithoutServiceAccount_FailsWithGuidance()
    {
        var prevGac = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", null);
        try
        {
            var producer = CreateProducer("operation=CreateDownloadLink&objectName=x.txt");
            await producer.Start();
            var act = () => producer.Process(new Exchange(new Message()));

            await act.Should().ThrowAsync<InvalidOperationException>()
                .Where(e => e.Message.Contains("sign"),
                    "без сервис-аккаунта подписывать нечем — ошибка обязана это объяснять");
            await producer.Stop();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", prevGac);
        }
    }

    // ═══ Д3: bucket operations ═══

    [Fact]
    public async Task CreateBucket_ThenDeleteBucket_Roundtrip()
    {
        var bucket = $"ops-roundtrip-{Guid.NewGuid():N}";

        var create = CreateProducerForBucket(bucket, $"operation=CreateBucket&projectId={ProjectId}");
        await create.Start();
        await create.Process(new Exchange(new Message()));
        await create.Stop();

        (await _rawGcs!.GetBucketAsync(bucket)).Should().NotBeNull();

        var delete = CreateProducerForBucket(bucket, $"operation=DeleteBucket&projectId={ProjectId}");
        await delete.Start();
        await delete.Process(new Exchange(new Message()));
        await delete.Stop();

        var act = () => _rawGcs.GetBucketAsync(bucket);
        await act.Should().ThrowAsync<Google.GoogleApiException>();
    }

    [Fact]
    public async Task ListBuckets_ReturnsNamesIncludingTestBucket()
    {
        var producer = CreateProducer($"operation=ListBuckets&projectId={ProjectId}");
        await producer.Start();
        var exchange = new Exchange(new Message());
        await producer.Process(exchange);
        await producer.Stop();

        var names = exchange.Out!.Body as List<string>;
        names.Should().NotBeNull().And.Contain(TestBucket);
        exchange.Out.Headers[FirebaseStorageHeaders.BucketCount].Should().Be(names!.Count);
    }

    [Fact]
    public async Task Consumer_AutoCreateBucket_CreatesMissingBucket()
    {
        var bucket = $"auto-created-{Guid.NewGuid():N}";
        var component = new FirebaseStorageComponent { CredentialProvider = _prodProvider! };
        var uri = EndpointUriParser.Parse(
            $"fbstorage://{bucket}?autoCreateBucket=true&projectId={ProjectId}" +
            "&delay=60000&initialDelay=100&includeBody=false");
        var endpoint = (FirebaseStorageEndpoint)component.CreateEndpoint(uri);
        var consumer = endpoint.CreateConsumer(new NoOpProcessor());

        await consumer.Start();
        await Task.Delay(300);
        await consumer.Stop();

        (await _rawGcs!.GetBucketAsync(bucket)).Should().NotBeNull(
            "autoCreateBucket=true обязан создавать бакет на старте консьюмера (паритет с S3)");
    }

    // ═══ Helpers ═══

    private sealed class NoOpProcessor : redb.Route.Abstractions.IProcessor
    {
        public Task Process(redb.Route.Abstractions.IExchange exchange, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private FirebaseStorageProducer CreateProducerForBucket(string bucket, string queryParams)
    {
        var component = new FirebaseStorageComponent { CredentialProvider = _prodProvider! };
        var uri = EndpointUriParser.Parse($"fbstorage://{bucket}?{queryParams}");
        var endpoint = (FirebaseStorageEndpoint)component.CreateEndpoint(uri);
        return (FirebaseStorageProducer)endpoint.CreateProducer();
    }

    private static string WriteFakeServiceAccount()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var b64 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        var pemBody = string.Join("\n", System.Text.RegularExpressions.Regex
            .Matches(b64, ".{1,64}").Select(m => m.Value));
        var pem = $"-----BEGIN PRIVATE KEY-----\n{pemBody}\n-----END PRIVATE KEY-----\n";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = ProjectId,
            private_key_id = "fake-key-id",
            private_key = pem,
            client_email = $"unit@{ProjectId}.iam.gserviceaccount.com",
            client_id = "0",
            token_uri = "https://oauth2.googleapis.com/token",
        });
        var path = Path.Combine(Path.GetTempPath(), $"fake-sa-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private FirebaseStorageProducer CreateProducer(string queryParams)
    {
        var component = new FirebaseStorageComponent { CredentialProvider = _prodProvider! };
        var uri = EndpointUriParser.Parse($"fbstorage://{TestBucket}?{queryParams}");
        var endpoint = (FirebaseStorageEndpoint)component.CreateEndpoint(uri);
        return (FirebaseStorageProducer)endpoint.CreateProducer();
    }

    private async Task SeedAsync(string name, string content)
    {
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await _rawGcs!.UploadObjectAsync(TestBucket, name, "text/plain", data);
    }

    private async Task<string> ReadAsync(string name)
    {
        using var ms = new MemoryStream();
        await _rawGcs!.DownloadObjectAsync(TestBucket, name, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
