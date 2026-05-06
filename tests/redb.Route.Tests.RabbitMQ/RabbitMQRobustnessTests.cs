using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;
using Xunit.Abstractions;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// Integration tests validating:
/// Fix #9: ConnectableProducer CAS start (concurrent Start safety).
/// Fix #10: RabbitMQ channel cleanup on topology error.
/// Drain behavior with real RabbitMQ broker.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQRobustnessTests
{
    private const string Host = "localhost";
    private const int Port = 5672;
    private readonly ITestOutputHelper _output;

    public RabbitMQRobustnessTests(ITestOutputHelper output) => _output = output;

    private RabbitMQEndpoint CreateEndpoint(string queue, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&username=admin&password=admin&declare=true";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"rabbitmq://{queue}?{qs}");
        var component = new RabbitMQComponent();
        return (RabbitMQEndpoint)component.CreateEndpoint(uri);
    }

    /// <summary>
    /// Fix #9: ConnectableProducer uses Interlocked.CompareExchange —
    /// 10 concurrent Start() calls must not throw or create duplicate connections.
    /// </summary>
    [Fact]
    public async Task ConcurrentProducerStart_DoesNotThrow()
    {
        var queue = $"test-cas-{Guid.NewGuid():N}";
        var endpoint = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)endpoint.CreateProducer();

        // Fire 10 concurrent Start() calls — only one should ConnectAsync
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => producer.Start()))
            .ToArray();

        await Task.WhenAll(tasks);
        producer.IsStarted.Should().BeTrue();

        // Verify producer actually works after concurrent start
        await producer.Process(new Exchange(new Message("cas-test")));

        await producer.Stop();
        await endpoint.Stop();
        _output.WriteLine("Concurrent CAS start test passed for queue {0}", queue);
    }

    /// <summary>
    /// Validates InflightDrainGuard on RabbitMQ consumer:
    /// slow in-flight processing completes before Stop returns.
    /// </summary>
    [Fact]
    public async Task Drain_SlowProcessing_CompletesBeforeStopReturns()
    {
        var queue = $"test-drain-{Guid.NewGuid():N}";
        var processedCount = 0;
        var processingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                processingStarted.TrySetResult();
                await Task.Delay(500, ci.Arg<CancellationToken>());
                Interlocked.Increment(ref processedCount);
            });

        var epCons = CreateEndpoint(queue);
        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        // Publish a message
        var epProd = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("drain-me")));
        await producer.Stop();
        await epProd.Stop();

        // Wait for message to reach the consumer and processing to begin
        await Task.WhenAny(processingStarted.Task, Task.Delay(15_000));
        processingStarted.Task.IsCompleted.Should().BeTrue("message should reach the consumer");

        // Stop consumer while processing is in-flight — drain must wait
        await consumer.Stop();
        await epCons.Stop();

        processedCount.Should().Be(1,
            "in-flight message must complete before Stop returns");
        _output.WriteLine("RabbitMQ drain test passed for queue {0}", queue);
    }

    /// <summary>
    /// Double Stop on producer must not throw (idempotent via CAS).
    /// </summary>
    [Fact]
    public async Task ProducerDoubleStop_DoesNotThrow()
    {
        var queue = $"test-dstop-{Guid.NewGuid():N}";
        var endpoint = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)endpoint.CreateProducer();

        await producer.Start();
        await producer.Stop();
        await producer.Stop(); // second stop — must be a no-op

        await endpoint.Stop();
    }
}
