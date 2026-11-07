using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Sqs;
using redb.Route.Transactions;
using Message = redb.Route.Core.Message;
using SnsDsl = redb.Route.Sqs.Fluent.Sns;
using SqsDsl = redb.Route.Sqs.Fluent.Sqs;

namespace redb.Route.Tests.Sqs;

/// <summary>
/// SQS sends and SNS publishes inside <c>.Transacted()</c>: unset, they join the block and leave only after the database
/// commits; <c>transacted=false</c> sends at once; <c>transacted=true</c> outside a block is refused.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqsTransactedSendTests
{
    private const string ServiceUrl = "http://localhost:4566";
    private const string Region = "us-east-1";

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static IAmazonSQS RawSqs() =>
        new AmazonSQSClient(new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = Region });

    private static IProducer SqsProducer(string queue, bool? transacted)
    {
        var builder = SqsDsl.Queue(queue).ServiceUrl(ServiceUrl).Region(Region).Credentials("test", "test").AutoCreateQueue();
        if (transacted is { } value) builder = builder.Transacted(value);
        return new SqsComponent().CreateEndpoint(EndpointUriParser.Parse(builder.Build())).CreateProducer();
    }

    private static Task InTransaction(IExchange exchange, Func<IExchange, CancellationToken, Task> body) =>
        new TransactedProcessor(new DelegateProcessor(body), new TransactionPolicy()).Process(exchange);

    private static async Task SendFromAFailedBlock(IProducer producer, string body)
    {
        await producer.Start();
        var failed = () => InTransaction(new Exchange(new Message(body)), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            throw new InvalidOperationException("the unit of work failed");
        });
        await failed.Should().ThrowAsync<InvalidOperationException>();
        await producer.Stop();
    }

    private static async Task<List<string>> ReceiveAll(IAmazonSQS sqs, string queueUrl)
    {
        var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 3,
        });
        return (response.Messages ?? []).Select(m => m.Body).ToList();
    }

    [Fact]
    public async Task Sqs_send_without_transacted_joins_the_block_and_leaves_nothing_when_it_fails()
    {
        var queue = UniqueName("tx-join");
        using var sqs = RawSqs();
        var queueUrl = (await sqs.CreateQueueAsync(queue)).QueueUrl;

        await SendFromAFailedBlock(SqsProducer(queue, transacted: null), "sent-from-a-failed-block");

        (await ReceiveAll(sqs, queueUrl)).Should().BeEmpty(
            "inside .Transacted() a send joins the transaction unless transacted=false opts out");
    }

    [Fact]
    public async Task Sqs_send_with_transacted_false_goes_out_at_once_even_from_a_failed_block()
    {
        var queue = UniqueName("tx-optout");
        using var sqs = RawSqs();
        var queueUrl = (await sqs.CreateQueueAsync(queue)).QueueUrl;

        await SendFromAFailedBlock(SqsProducer(queue, transacted: false), "sent-from-a-failed-block");

        (await ReceiveAll(sqs, queueUrl)).Should().Equal("sent-from-a-failed-block");
    }

    [Fact]
    public async Task Sqs_send_inside_a_block_goes_out_after_the_commit()
    {
        var queue = UniqueName("tx-commit");
        using var sqs = RawSqs();
        var queueUrl = (await sqs.CreateQueueAsync(queue)).QueueUrl;
        var producer = SqsProducer(queue, transacted: null);
        await producer.Start();
        List<string>? insideTheBlock = null;

        await InTransaction(new Exchange(new Message("committed")), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            insideTheBlock = await ReceiveAll(sqs, queueUrl);
        });

        insideTheBlock.Should().BeEmpty("nothing leaves before the database has committed");
        (await ReceiveAll(sqs, queueUrl)).Should().Equal("committed");
        await producer.Stop();
    }

    [Fact]
    public async Task Sqs_send_with_transacted_true_outside_a_block_is_refused()
    {
        var producer = SqsProducer(UniqueName("tx-none"), transacted: true);
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message("nobody would commit this")));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Transacted()*");
        await producer.Stop();
    }

    [Fact]
    public async Task Sns_publish_without_transacted_joins_the_block_and_leaves_nothing_when_it_fails()
    {
        var (sqs, queueUrl, producer) = await SnsSubscribedToAQueue(transacted: null);
        using (sqs)
        {
            await SendFromAFailedBlock(producer, "published-from-a-failed-block");

            (await ReceiveAll(sqs, queueUrl)).Should().BeEmpty(
                "inside .Transacted() a publish joins the transaction unless transacted=false opts out");
        }
    }

    [Fact]
    public async Task Sns_publish_with_transacted_false_goes_out_at_once_even_from_a_failed_block()
    {
        var (sqs, queueUrl, producer) = await SnsSubscribedToAQueue(transacted: false);
        using (sqs)
        {
            await SendFromAFailedBlock(producer, "published-from-a-failed-block");

            (await ReceiveAll(sqs, queueUrl)).Should().ContainSingle().Which.Should().Be("published-from-a-failed-block");
        }
    }

    [Fact]
    public async Task Sqs_consumer_does_not_delete_a_rolled_back_unit_of_work()
    {
        var queue = UniqueName("tx-rbonly");
        using var sqs = RawSqs();
        var queueUrl = (await sqs.CreateQueueAsync(queue)).QueueUrl;
        await sqs.SendMessageAsync(queueUrl, "rolled-back");

        // What .RollbackAll() does inside .Transacted(): mark the unit of work rollback-only and stop the route.
        var deliveries = 0;
        var deliveredAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var route = new TransactedProcessor(new DelegateProcessor(ex =>
        {
            if (Interlocked.Increment(ref deliveries) >= 2) deliveredAgain.TrySetResult();
            ex.MarkRollbackOnly();
            ex.Stop();
        }), new TransactionPolicy());

        var builder = SqsDsl.Queue(queue).ServiceUrl(ServiceUrl).Region(Region).Credentials("test", "test")
            .WaitTimeSeconds(1).ResetVisibilityOnFailure();
        var consumer = new SqsComponent().CreateEndpoint(EndpointUriParser.Parse(builder.Build())).CreateConsumer(route);
        await consumer.Start();
        await Task.WhenAny(deliveredAgain.Task, Task.Delay(15_000));
        await consumer.Stop();

        deliveries.Should().BeGreaterThanOrEqualTo(2,
            "a rolled-back unit of work is not deleted, so the queue delivers the message again");
    }

    private static async Task<(IAmazonSQS Sqs, string QueueUrl, IProducer Producer)> SnsSubscribedToAQueue(bool? transacted)
    {
        var sqs = RawSqs();
        var queueUrl = (await sqs.CreateQueueAsync(UniqueName("snsq"))).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["QueueArn"],
        })).Attributes["QueueArn"];

        var builder = SnsDsl.Topic(UniqueName("evt"))
            .ServiceUrl(ServiceUrl).Region(Region).Credentials("test", "test")
            .AutoCreateTopic().SubscribeSnsToSqs(arn).RawMessageDelivery();
        if (transacted is { } value) builder = builder.Transacted(value);
        var producer = new SnsComponent().CreateEndpoint(EndpointUriParser.Parse(builder.Build())).CreateProducer();
        return (sqs, queueUrl, producer);
    }
}
