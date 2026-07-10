using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sqs;
using Xunit.Abstractions;
using Message = redb.Route.Core.Message;
using SqsDsl = redb.Route.Sqs.Fluent.Sqs;
using SnsDsl = redb.Route.Sqs.Fluent.Sns;

namespace redb.Route.Tests.Sqs;

/// <summary>
/// Integration tests for the SQS/SNS connector against LocalStack.
/// Requires: docker compose -f C:\Work\yaml\Amazon\docker-compose.yml up -d
/// Endpoint: http://localhost:4566 (community, anonymous test/test creds).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqsIntegrationTests
{
    private const string ServiceUrl = "http://localhost:4566";
    private const string Region = "us-east-1";

    private readonly ITestOutputHelper _output;
    public SqsIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static IAmazonSQS RawSqs() =>
        new AmazonSQSClient(new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = Region });

    // Base builder: serviceUrl + region + creds + autoCreate always applied; chain extras inline.
    private static redb.Route.Sqs.Fluent.SqsBuilder Q(string name) =>
        SqsDsl.Queue(name).ServiceUrl(ServiceUrl).Region(Region).Credentials("test", "test").AutoCreateQueue();

    private static SqsEndpoint MakeEndpoint(redb.Route.Sqs.Fluent.SqsBuilder b) =>
        (SqsEndpoint)new SqsComponent().CreateEndpoint(EndpointUriParser.Parse(b.Build()));

    private static IProcessor Recorder(Func<IExchange, Task> onEach)
    {
        var p = Substitute.For<IProcessor>();
        p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(call => onEach(call.Arg<IExchange>()));
        return p;
    }

    private static async Task WaitFor(Func<bool> cond, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cond())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    // ── Roundtrip ─────────────────────────────────────────────────────

    [Fact]
    public async Task Roundtrip_SendReceive_BodyAndAttributes()
    {
        var queue = UniqueName("rt");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = MakeEndpoint(Q(queue)).CreateConsumer(Recorder(ex =>
        {
            received.Add(ex);
            tcs.TrySetResult();
            return Task.CompletedTask;
        }));
        await consumer.Start();
        try
        {
            var prod = MakeEndpoint(Q(queue));
            var producer = prod.CreateProducer();
            await producer.Start();

            var exchange = new Exchange(new Message("hello-sqs"));
            exchange.In.Headers["orderId"] = "42";
            await producer.Process(exchange);

            await Task.WhenAny(tcs.Task, Task.Delay(15_000));
            received.Should().NotBeEmpty();
            var got = received.First();
            got.In.Body!.ToString().Should().Be("hello-sqs");
            got.In.Headers[SqsHeaders.MessageAttributePrefix + "orderId"].Should().Be("42");
            await producer.Stop();
        }
        finally { await consumer.Stop(); }
    }

    // ── Concurrency ───────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentConsumers_ProcessUpToNInParallel()
    {
        const int pool = 5, total = 20;
        var queue = UniqueName("conc");
        var current = 0; var max = 0; var maxLock = new object(); var received = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // maxNumberOfMessages=1 so each of the N workers holds one message at a time — the pool truly saturates.
        var consumer = MakeEndpoint(Q(queue).ConcurrentConsumers(pool).MaxNumberOfMessages(1).WaitTimeSeconds(1))
            .CreateConsumer(Recorder(async _ =>
            {
                var c = Interlocked.Increment(ref current);
                lock (maxLock) { if (c > max) max = c; }
                await Task.Delay(200);
                Interlocked.Decrement(ref current);
                if (Interlocked.Increment(ref received) >= total) done.TrySetResult();
            }));
        await consumer.Start();
        try
        {
            var producer = MakeEndpoint(Q(queue)).CreateProducer();
            await producer.Start();
            for (var i = 0; i < total; i++)
                await producer.Process(new Exchange(new Message($"m{i}")));
            await producer.Stop();

            await Task.WhenAny(done.Task, Task.Delay(30_000));
            Volatile.Read(ref received).Should().Be(total, "no message lost");
            max.Should().Be(pool, "the pool saturates to N concurrent workers");
            _output.WriteLine("max concurrency = {0}, received = {1}", max, received);
        }
        finally { await consumer.Stop(); }
    }

    // ── FIFO ordering ─────────────────────────────────────────────────

    [Fact]
    public async Task Fifo_SingleConsumer_PreservesOrder()
    {
        const int total = 10;
        var queue = UniqueName("ord") + ".fifo";
        var received = new List<int>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = MakeEndpoint(Q(queue).ConcurrentConsumers(1).MaxNumberOfMessages(1))
            .CreateConsumer(Recorder(ex =>
            {
                lock (received)
                {
                    received.Add(int.Parse(ex.In.Body!.ToString()!));
                    if (received.Count >= total) done.TrySetResult();
                }
                return Task.CompletedTask;
            }));
        await consumer.Start();
        try
        {
            var producer = MakeEndpoint(Q(queue).MessageGroupId("g1")).CreateProducer();
            await producer.Start();
            for (var i = 0; i < total; i++)
                await producer.Process(new Exchange(new Message(i.ToString())));
            await producer.Stop();

            await Task.WhenAny(done.Task, Task.Delay(30_000));
            received.Should().HaveCount(total);
            received.Should().BeInAscendingOrder("a FIFO queue with one consumer preserves order");
        }
        finally { await consumer.Stop(); }
    }

    // ── Batch send ────────────────────────────────────────────────────

    [Fact]
    public async Task Batch_SendsEnumerableAsOneCall_AllReceived()
    {
        var queue = UniqueName("batch");
        var received = new ConcurrentBag<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = MakeEndpoint(Q(queue)).CreateConsumer(Recorder(ex =>
        {
            received.Add(ex.In.Body!.ToString()!);
            if (received.Count >= 5) done.TrySetResult();
            return Task.CompletedTask;
        }));
        await consumer.Start();
        try
        {
            var producer = MakeEndpoint(Q(queue).EnableBatch()).CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message(new[] { "a", "b", "c", "d", "e" })));
            await producer.Stop();

            await Task.WhenAny(done.Task, Task.Delay(15_000));
            received.Should().BeEquivalentTo(["a", "b", "c", "d", "e"]);
        }
        finally { await consumer.Stop(); }
    }

    // ── Visibility redelivery on failure (at-least-once) ──────────────

    [Fact]
    public async Task Failure_LeavesMessageForRedelivery_ThenSucceeds()
    {
        var queue = UniqueName("vis");
        var attempts = 0;
        var succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // visibilityTimeout=2s + resetVisibilityOnFailure so the first (throwing) receive redelivers quickly.
        var consumer = MakeEndpoint(Q(queue).VisibilityTimeout(2).ResetVisibilityOnFailure().MaxNumberOfMessages(1))
            .CreateConsumer(Recorder(_ =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n == 1) throw new InvalidOperationException("boom on first attempt");
                succeeded.TrySetResult();
                return Task.CompletedTask;
            }));
        await consumer.Start();
        try
        {
            var producer = MakeEndpoint(Q(queue)).CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("retry-me")));
            await producer.Stop();

            await Task.WhenAny(succeeded.Task, Task.Delay(30_000));
            Volatile.Read(ref attempts).Should().BeGreaterThanOrEqualTo(2,
                "a failed message is not deleted and is redelivered after the visibility timeout");
        }
        finally { await consumer.Stop(); }
    }

    // ── SNS → SQS fan-out ─────────────────────────────────────────────

    [Fact]
    public async Task SnsToSqs_PublishReachesSubscribedQueue()
    {
        var queue = UniqueName("snsq");
        var topic = UniqueName("evt");

        // Create the queue up front and read its ARN for the subscription.
        using var raw = RawSqs();
        var queueUrl = (await raw.CreateQueueAsync(queue)).QueueUrl;
        var arn = (await raw.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["QueueArn"],
        })).Attributes["QueueArn"];

        var received = new ConcurrentBag<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = MakeEndpoint(Q(queue)).CreateConsumer(Recorder(ex =>
        {
            received.Add(ex.In.Body!.ToString()!);
            done.TrySetResult();
            return Task.CompletedTask;
        }));
        await consumer.Start();
        try
        {
            var snsUri = SnsDsl.Topic(topic)
                .ServiceUrl(ServiceUrl).Region(Region).Credentials("test", "test")
                .AutoCreateTopic().SubscribeSnsToSqs(arn).Build();
            var snsEndpoint = (SnsEndpoint)new SnsComponent().CreateEndpoint(EndpointUriParser.Parse(snsUri));
            var producer = snsEndpoint.CreateProducer();
            await producer.Start(); // subscribes the queue to the topic

            await producer.Process(new Exchange(new Message("via-sns")));

            await Task.WhenAny(done.Task, Task.Delay(20_000));
            received.Should().NotBeEmpty("the SNS message must fan out to the subscribed SQS queue");
            // SNS delivers a JSON envelope; the payload is inside "Message".
            received.First().Should().Contain("via-sns");
            await producer.Stop();
        }
        finally { await consumer.Stop(); }
    }

    // ── SNS → SQS raw message delivery (bare payload + attribute passthrough) ──

    [Fact]
    public async Task SnsToSqs_RawDelivery_DeliversBarePayloadAndAttributes()
    {
        var queue = UniqueName("rawq");
        var topic = UniqueName("rawevt");

        // Create the queue up front and read its ARN for the subscription.
        using var raw = RawSqs();
        var queueUrl = (await raw.CreateQueueAsync(queue)).QueueUrl;
        var arn = (await raw.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["QueueArn"],
        })).Attributes["QueueArn"];

        var received = new ConcurrentBag<IExchange>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = MakeEndpoint(Q(queue)).CreateConsumer(Recorder(ex =>
        {
            received.Add(ex);
            done.TrySetResult();
            return Task.CompletedTask;
        }));
        await consumer.Start();
        try
        {
            // rawMessageDelivery: the subscription is set to RawMessageDelivery=true, so the queue gets
            // the bare payload (not the JSON envelope) and SNS attributes arrive as SQS attributes.
            var snsUri = SnsDsl.Topic(topic)
                .ServiceUrl(ServiceUrl).Region(Region).Credentials("test", "test")
                .AutoCreateTopic().SubscribeSnsToSqs(arn).RawMessageDelivery().Build();
            var snsEndpoint = (SnsEndpoint)new SnsComponent().CreateEndpoint(EndpointUriParser.Parse(snsUri));
            var producer = snsEndpoint.CreateProducer();
            await producer.Start(); // subscribes the queue with RawMessageDelivery=true

            var exchange = new Exchange(new Message("bare-payload"));
            exchange.In.Headers["eventType"] = "created";
            await producer.Process(exchange);

            await Task.WhenAny(done.Task, Task.Delay(20_000));
            received.Should().NotBeEmpty("the SNS message must fan out to the subscribed SQS queue");
            var got = received.First();
            // Raw delivery → the body is the exact payload, NOT a {"Type":"Notification",...} envelope.
            got.In.Body!.ToString().Should().Be("bare-payload");
            // SNS message attributes are delivered as SQS message attributes (the envelope mode hides them).
            got.In.Headers[SqsHeaders.MessageAttributePrefix + "eventType"].Should().Be("created");
            await producer.Stop();
        }
        finally { await consumer.Stop(); }
    }

    // ── Transacted ack (SqsAckAction) ─────────────────────────────────

    [Fact]
    public async Task AckAction_CommitDeletes_RollbackRedelivers()
    {
        var queue = UniqueName("ack");
        using var raw = RawSqs();
        var url = (await raw.CreateQueueAsync(queue)).QueueUrl;

        await raw.SendMessageAsync(url, "commit-me");
        await raw.SendMessageAsync(url, "rollback-me");

        // Receive both with a comfortable visibility window.
        var msgs = new List<Amazon.SQS.Model.Message>();
        var recvDeadline = DateTime.UtcNow.AddSeconds(15);
        while (msgs.Count < 2 && DateTime.UtcNow < recvDeadline)
        {
            var r = await raw.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, VisibilityTimeout = 30,
            });
            if (r.Messages is not null) msgs.AddRange(r.Messages);
        }
        msgs.Should().HaveCount(2);
        var commit = msgs.First(m => m.Body == "commit-me");
        var rollback = msgs.First(m => m.Body == "rollback-me");

        // Commit deletes; Rollback resets visibility to 0 → immediate redelivery.
        await new SqsAckAction(raw, url, commit.ReceiptHandle, deleteAfterRead: true).Commit();
        await new SqsAckAction(raw, url, rollback.ReceiptHandle, deleteAfterRead: true).Rollback();

        var seen = new HashSet<string>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !seen.Contains("rollback-me"))
        {
            var r = await raw.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2,
            });
            foreach (var m in r.Messages ?? [])
            {
                seen.Add(m.Body);
                await raw.DeleteMessageAsync(url, m.ReceiptHandle);
            }
        }

        seen.Should().Contain("rollback-me", "rollback reset visibility → the message is redelivered");
        seen.Should().NotContain("commit-me", "commit deleted the message");
    }

    // ── Distributed trace propagation ─────────────────────────────────

    [Fact]
    public async Task TraceContext_Producer_Injects_Consumer_Surfaces()
    {
        // Listen to the redb.Route source so producer/consumer spans (and thus injection) are created.
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "redb.Route",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var queue = UniqueName("trace");
        string? traceparent = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = MakeEndpoint(Q(queue)).CreateConsumer(Recorder(ex =>
        {
            traceparent = ex.In.Headers.TryGetValue(SqsHeaders.MessageAttributePrefix + "traceparent", out var v)
                ? v?.ToString() : null;
            done.TrySetResult();
            return Task.CompletedTask;
        }));
        await consumer.Start();
        try
        {
            var producer = MakeEndpoint(Q(queue)).CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("traced")));
            await producer.Stop();

            await Task.WhenAny(done.Task, Task.Delay(15_000));
            traceparent.Should().NotBeNullOrEmpty(
                "the producer injects W3C traceparent as a message attribute so the trace continues across SQS");
        }
        finally { await consumer.Stop(); }
    }
}
