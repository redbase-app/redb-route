using System.Linq;
using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="ToProcessor"/>.</summary>
public class ToProcessorTests
{
    /// <summary>Sends exchange to the endpoint producer.</summary>
    [Fact]
    public async Task Process_SendsToProducer()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("kafka://orders").Returns(endpoint);

        var processor = new ToProcessor("kafka://orders", context);
        var exchange = new Exchange(new Message("order-1"));

        await processor.Process(exchange);

        await producer.Received(1).Process(exchange, Arg.Any<CancellationToken>());
    }

    /// <summary>Producer is cached after first use.</summary>
    [Fact]
    public async Task Process_CachesProducer()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("kafka://orders").Returns(endpoint);

        var processor = new ToProcessor("kafka://orders", context);

        await processor.Process(new Exchange());
        await processor.Process(new Exchange());

        endpoint.Received(1).CreateProducer(); // Only once
    }

    /// <summary>EndpointUri property returns the configured URI.</summary>
    [Fact]
    public void EndpointUri_ReturnsConfigured()
    {
        var context = Substitute.For<IRouteContext>();
        var processor = new ToProcessor("redis:SET:key", context);

        processor.EndpointUri.Should().Be("redis:SET:key");
    }

    /// <summary>Null URI throws.</summary>
    [Fact]
    public void Constructor_NullUri_Throws()
    {
        var context = Substitute.For<IRouteContext>();
        var act = () => new ToProcessor(null!, context);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Null context throws.</summary>
    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new ToProcessor("kafka://x", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>RecordMessageOut is called on the endpoint after successful processing.</summary>
    [Fact]
    public async Task Process_Success_RecordsMessageOut()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint, IEndpointStatistics>();
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("direct://stats").Returns(endpoint);

        var processor = new ToProcessor("direct://stats", context);
        await processor.Process(new Exchange(new Message("data")));

        ((IEndpointStatistics)endpoint).Received(1).RecordMessageOut();
        ((IEndpointStatistics)endpoint).DidNotReceive().RecordError();
    }

    /// <summary>RecordError is called on the endpoint when the producer throws.</summary>
    [Fact]
    public async Task Process_Error_RecordsError()
    {
        var producer = Substitute.For<IProducer>();
        producer.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        var endpoint = Substitute.For<IEndpoint, IEndpointStatistics>();
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("direct://stats-err").Returns(endpoint);

        var processor = new ToProcessor("direct://stats-err", context);
        var act = () => processor.Process(new Exchange(new Message("data")));
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Production records the error WITH the exception (ToProcessor calls RecordError(ex)),
        // so assert the Exception overload — RecordError() (parameterless) is a different call.
        ((IEndpointStatistics)endpoint).Received(1).RecordError(Arg.Any<Exception>());
        ((IEndpointStatistics)endpoint).DidNotReceive().RecordMessageOut();
    }

    /// <summary>No error when endpoint does not implement IEndpointStatistics.</summary>
    [Fact]
    public async Task Process_NonStatsEndpoint_NoError()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>(); // No IEndpointStatistics
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("mock://no-stats").Returns(endpoint);

        var processor = new ToProcessor("mock://no-stats", context);
        await processor.Process(new Exchange(new Message("data")));

        // Should not throw — graceful when endpoint doesn't implement stats
        await producer.Received(1).Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());
    }

    // ── Cold-start race regression (RabbitMQ 3.2.2 native concurrency exposed this) ────

    /// <summary>
    /// Under concurrent cold start the producer must be started exactly once, and NO exchange may reach
    /// <c>producer.Process()</c> before <c>Start()</c> has fully completed — otherwise a half-initialised
    /// producer (e.g. a Redis producer whose <c>_db</c> is still null) throws an NRE. Regression for the
    /// lazy-start race that surfaced once RabbitMQ <c>ConcurrentConsumers(N)</c> actually parallelised.
    /// </summary>
    [Fact]
    public async Task Process_ConcurrentColdStart_StartsOnce_AndNeverProcessesBeforeStartCompletes()
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var startCalls = 0;
        var processedBeforeStart = 0;

        var producer = Substitute.For<IProducer>();
        // Start() blocks until the gate is released, then marks the producer "started".
        producer.Start(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            Interlocked.Increment(ref startCalls);
            await startGate.Task.ConfigureAwait(false);
            Volatile.Write(ref started, 1);
        });
        // Process() records any call that arrives before Start() has completed — that must never happen.
        producer.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Volatile.Read(ref started) == 0) Interlocked.Increment(ref processedBeforeStart);
            return Task.CompletedTask;
        });

        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("redis:SET:k").Returns(endpoint);

        var to = new ToProcessor("redis:SET:k", context);

        // 20 exchanges hit the cold producer at once (mirrors ConcurrentConsumers(20) dispatch).
        var calls = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => to.Process(new Exchange(new Message())))).ToArray();
        await Task.Delay(50); // let them all pile up on the single in-flight start
        startGate.SetResult();
        await Task.WhenAll(calls);

        startCalls.Should().Be(1, "single-flight: the producer is created + started exactly once");
        endpoint.Received(1).CreateProducer();
        processedBeforeStart.Should().Be(0, "no exchange may reach Process() before Start() fully completes");
        await producer.Received(20).Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A <see cref="ConnectableProducer"/> must not report <c>IsStarted</c> until <c>ConnectAsync</c> has
    /// completed, and two concurrent <c>Start()</c> calls must await the SAME connect (connect once).
    /// </summary>
    [Fact]
    public async Task ConnectableProducer_NotStartedUntilConnectCompletes_ConcurrentStartConnectsOnce()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoint = Substitute.For<IEndpoint>();
        var producer = new GatedProducer(gate, endpoint);

        var s1 = producer.Start();
        var s2 = producer.Start(); // concurrent — must join the same connect, not start a second
        await Task.Delay(30);

        producer.IsStarted.Should().BeFalse("ConnectAsync has not completed — must not report started");

        gate.SetResult();
        await Task.WhenAll(s1, s2);

        producer.IsStarted.Should().BeTrue();
        producer.Ready.Should().BeTrue("resources are assigned inside ConnectAsync");
        producer.ConnectCalls.Should().Be(1, "concurrent Start() calls await the same ConnectAsync");
    }

    /// <summary>Test double: a ConnectableProducer whose ConnectAsync is gated and assigns state only when released.</summary>
    private sealed class GatedProducer : ConnectableProducer
    {
        private readonly TaskCompletionSource _gate;
        private readonly IEndpoint _endpoint;
        public int ConnectCalls;
        public volatile bool Ready;

        public GatedProducer(TaskCompletionSource gate, IEndpoint endpoint)
        {
            _gate = gate;
            _endpoint = endpoint;
        }

        protected override IEndpoint ProducerEndpoint => _endpoint;
        protected override string ProducerName => "test:gated";

        protected override async Task ConnectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref ConnectCalls);
            await _gate.Task.ConfigureAwait(false); // simulate a slow connection
            Ready = true;                            // resources assigned ONLY after the connect settles
        }

        public override Task Process(IExchange exchange, CancellationToken ct = default)
        {
            EnsureStarted();
            return Task.CompletedTask;
        }
    }
}
