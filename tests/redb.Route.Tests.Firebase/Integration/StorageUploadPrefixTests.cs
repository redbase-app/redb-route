using System.Text;
using Google.Cloud.Storage.V1;
using redb.Route.Core;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// Ф11 волна Г7: the URI path after the bucket is a folder-like prefix — Upload/Move must
/// join it with <c>/</c>, so <c>fbstorage://bucket/uploads</c> + <c>file.txt</c> yields
/// <c>uploads/file.txt</c>, never <c>uploadsfile.txt</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class StorageUploadPrefixTests : IAsyncLifetime
{
    private const string ProjectId = "demo-redb";
    private const string GcsEndpoint = "http://localhost:4443/storage/v1/";
    private static readonly string TestBucket = $"prefix-tests-net{Environment.Version.Major}";

    private readonly ITestOutputHelper _output;
    private StorageClient? _rawGcs;
    private FirebaseCredentialProvider? _prodProvider;
    private string? _prevEmuVar;

    public StorageUploadPrefixTests(ITestOutputHelper output) => _output = output;

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

    [Fact]
    public async Task Upload_UriPrefixWithoutSlash_ProducesFolderLikeObjectName()
    {
        var producer = CreateProducer($"fbstorage://{TestBucket}/uploads?operation=Upload&objectName=file.txt");

        await producer.Start();
        var exchange = new Exchange(new Message(Encoding.UTF8.GetBytes("data")));
        await producer.Process(exchange);
        await producer.Stop();

        var name = exchange.In.GetHeader<string>(FirebaseStorageHeaders.ObjectName);
        name.Should().Be("uploads/file.txt",
            "путь в URI — папко-подобный префикс и обязан склеиваться через '/'");
        _output.WriteLine($"Uploaded as: {name}");
    }

    [Fact]
    public async Task Upload_UriPrefixWithTrailingSlash_Unchanged()
    {
        var producer = CreateProducer($"fbstorage://{TestBucket}/uploads/?operation=Upload&objectName=file2.txt");

        await producer.Start();
        var exchange = new Exchange(new Message(Encoding.UTF8.GetBytes("data")));
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.GetHeader<string>(FirebaseStorageHeaders.ObjectName)
            .Should().Be("uploads/file2.txt");
    }

    [Fact]
    public async Task Download_StreamBody_IsTrueStreaming()
    {
        var name = $"stream-{Guid.NewGuid():N}.txt";
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes("streamed-content")))
            await _rawGcs!.UploadObjectAsync(TestBucket, name, "text/plain", data);

        var producer = CreateProducer(
            $"fbstorage://{TestBucket}?operation=Download&objectName={name}&streamBody=true");
        await producer.Start();
        var exchange = new Exchange(new Message());
        await producer.Process(exchange);

        var body = exchange.Out!.Body as Stream;
        body.Should().NotBeNull();
        body.Should().NotBeOfType<MemoryStream>(
            "StreamBody обязан отдавать настоящий поток, а не полную буферизацию в память (Д8)");

        using var reader = new StreamReader(body!, Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().Be("streamed-content");
        await producer.Stop();
    }

    [Fact]
    public async Task Download_RecordsBytesIn()
    {
        var name = $"bytes-{Guid.NewGuid():N}.txt";
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes("twelve bytes")))
            await _rawGcs!.UploadObjectAsync(TestBucket, name, "text/plain", data);

        var component = new FirebaseStorageComponent { CredentialProvider = _prodProvider! };
        var endpoint = (FirebaseStorageEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"fbstorage://{TestBucket}?operation=Download&objectName={name}"));
        var producer = (FirebaseStorageProducer)endpoint.CreateProducer();

        await producer.Start();
        await producer.Process(new Exchange(new Message()));
        await producer.Stop();

        ((redb.Route.Abstractions.IEndpointStatistics)endpoint).BytesIn.Should().Be(12,
            "producer-Download обязан писать принятые байты в статистику, как S3");
    }

    private FirebaseStorageProducer CreateProducer(string uri)
    {
        var component = new FirebaseStorageComponent { CredentialProvider = _prodProvider! };
        var endpoint = (FirebaseStorageEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(uri));
        return (FirebaseStorageProducer)endpoint.CreateProducer();
    }
}
