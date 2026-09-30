using RabbitMQ.Client;
using redb.Route.Core;
using redb.Route.RabbitMQ;
using Xunit.Abstractions;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// The broker closes a channel on a channel-level error — a publish to an exchange that does not exist is the everyday
/// case (404 NOT_FOUND). The connection stays up, so connection recovery does nothing for it. Expects RabbitMQ at
/// localhost:5672 (admin/admin).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQChannelRecoveryTests
{
    private readonly ITestOutputHelper _output;

    public RabbitMQChannelRecoveryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Producer_sends_again_after_the_broker_closed_its_channel()
    {
        var queue = $"test-chan-recover-{Guid.NewGuid():N}";
        await using (var setup = await ConnectAsync())
        await using (var channel = await setup.CreateChannelAsync())
        {
            await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(queue, "amq.direct", queue);
        }

        var endpoint = (RabbitMQEndpoint)new RabbitMQComponent().CreateEndpoint(EndpointUriParser.Parse(
            $"rabbitmq://?host=localhost&port=5672&username=admin&password=admin&exchange=${{header.target}}&routingKey={queue}"));
        var producer = (RabbitMQProducer)endpoint.CreateProducer();
        await producer.Start();

        var typo = new Exchange(new Message("to nowhere"));
        typo.In.Headers["target"] = $"no-such-exchange-{Guid.NewGuid():N}";
        var first = () => producer.Process(typo);
        (await first.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("NOT_FOUND");

        var good = new Exchange(new Message("delivered"));
        good.In.Headers["target"] = "amq.direct";
        await producer.Process(good);

        await producer.Stop();
        await endpoint.Stop();

        await using var check = await ConnectAsync();
        await using var checkChannel = await check.CreateChannelAsync();
        (await checkChannel.QueueDeclarePassiveAsync(queue)).MessageCount.Should().Be(1,
            "the next send opens a new channel instead of failing on the one the broker closed");
        await checkChannel.QueueDeleteAsync(queue);
    }

    [Fact]
    public async Task Consumer_says_so_when_the_broker_cancels_it_and_not_when_it_stops()
    {
        var logs = new CapturingLoggerFactory();
        var queue = $"test-cancelled-{Guid.NewGuid():N}";

        var (cancelled, cancelledEndpoint) = await StartConsumerAsync(logs, queue);
        await using (var admin = await ConnectAsync())
        await using (var channel = await admin.CreateChannelAsync())
            await channel.QueueDeleteAsync(queue);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!logs.Errors.Any(e => e.Contains("cancelled by the broker")) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await cancelled.Stop();
        await cancelledEndpoint.Stop();

        logs.Errors.Should().ContainSingle(e => e.Contains("cancelled by the broker") && e.Contains(queue),
            "a consumer whose queue was deleted would otherwise go quiet without a word");

        var quiet = new CapturingLoggerFactory();
        var (stopped, stoppedEndpoint) = await StartConsumerAsync(quiet, $"test-stopped-{Guid.NewGuid():N}");
        await stopped.Stop();
        await stoppedEndpoint.Stop();
        quiet.Errors.Should().BeEmpty("stopping the route cancels the subscription on purpose");
    }

    private static async Task<(RabbitMQConsumer, RabbitMQEndpoint)> StartConsumerAsync(CapturingLoggerFactory logs, string queue)
    {
        var context = new RouteContext(loggerFactory: logs);
        var component = new RabbitMQComponent();
        context.AddComponent(component);
        var endpoint = (RabbitMQEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(
            $"rabbitmq://{queue}?host=localhost&port=5672&username=admin&password=admin&declare=true&autoDelete=true"));
        var processor = Substitute.For<redb.Route.Abstractions.IProcessor>();
        var consumer = (RabbitMQConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        return (consumer, endpoint);
    }

    private sealed class CapturingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _errors = new();

        public IReadOnlyCollection<string> Errors => _errors.ToArray();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(_errors);

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> errors) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
                    errors.Enqueue(formatter(state, exception));
            }
        }
    }

    private static async Task<IConnection> ConnectAsync()
        => await new ConnectionFactory { HostName = "localhost", Port = 5672, UserName = "admin", Password = "admin" }.CreateConnectionAsync();
}
