using System.Collections.Concurrent;
using Amazon.S3;
using Amazon.S3.Model;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.S3;
using Xunit.Abstractions;

namespace redb.Route.Tests.S3;

/// <summary>
/// Ф11 Д4 (S3): the polling consumer can use a NAMED <see cref="IIdempotentRepository"/>
/// from the context registry — deduplication survives consumer restarts and, with a
/// persistent repository, process restarts and scale-out.
/// </summary>
[Trait("Category", "Integration")]
public sealed class S3SharedIdempotencyTests : IAsyncLifetime
{
    private const string ServiceUrl = "http://localhost:9000";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private const string Region = "us-east-1";
    private static readonly string TestBucket = $"shared-idem-net{Environment.Version.Major}";

    private readonly ITestOutputHelper _output;
    private IAmazonS3? _rawClient;

    public S3SharedIdempotencyTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _rawClient = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = Region,
        });
        try { await _rawClient.EnsureBucketExistsAsync(TestBucket); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
    }

    public Task DisposeAsync()
    {
        _rawClient?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task NamedRepository_DeduplicationSurvivesConsumerRestart()
    {
        var prefix = $"restart-{Guid.NewGuid():N}/";
        await _rawClient!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = TestBucket,
            Key = prefix + "obj.txt",
            ContentBody = "once",
        });

        await using var ctx = new RouteContext();
        ctx.AddComponent(new S3Component());
        ctx.AddIdempotentRepository("shared", new InMemoryIdempotentRepository());

        var endpoint = (S3Endpoint)ctx.GetEndpoint(
            $"s3://{TestBucket}?serviceUrl={ServiceUrl}&accessKey={AccessKey}&secretKey={SecretKey}" +
            $"&region={Region}&forcePathStyle=true&prefix={prefix}&delay=300&initialDelay=100" +
            "&idempotentRepository=shared&includeBody=false&deleteAfterRead=false");

        var received = new ConcurrentBag<string>();

        var first = endpoint.CreateConsumer(new CollectingProcessor(received));
        await first.Start();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(150);
        await first.Stop();

        var second = endpoint.CreateConsumer(new CollectingProcessor(received));
        await second.Start();
        await Task.Delay(1200);
        await second.Stop();

        received.Should().HaveCount(1,
            "именованный репозиторий обязан переживать рестарт консьюмера");
    }

    [Fact]
    public void UnknownRepositoryName_FailsLoud()
    {
        var ctx = new RouteContext();
        try
        {
            ctx.AddComponent(new S3Component());
            var endpoint = (S3Endpoint)ctx.GetEndpoint(
                $"s3://{TestBucket}?serviceUrl={ServiceUrl}&accessKey={AccessKey}&secretKey={SecretKey}" +
                $"&region={Region}&forcePathStyle=true&idempotentRepository=typo-name&delay=300");
            var consumer = endpoint.CreateConsumer(new CollectingProcessor(new ConcurrentBag<string>()));

            var act = () => consumer.Start();

            act.Should().ThrowAsync<InvalidOperationException>()
                .Where(e => e.Message.Contains("typo-name")).GetAwaiter().GetResult();
        }
        finally
        {
            ctx.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class CollectingProcessor : IProcessor
    {
        private readonly ConcurrentBag<string> _received;
        internal CollectingProcessor(ConcurrentBag<string> received) => _received = received;

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            _received.Add(exchange.In.GetHeader<string>(S3Headers.Key) ?? "?");
            return Task.CompletedTask;
        }
    }
}
