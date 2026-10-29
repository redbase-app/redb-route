using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.ErrorHandling;

namespace redb.Route.Tests.Core;

/// <summary>
/// The planned waits of the engine are marked in the in-flight registry (<see cref="InflightExchange.WaitingUntil"/>), so a
/// monitor such as the Tsak watchdog does not take an exchange that waits for its redelivery, or in a delay step, for a
/// hung one. After the wait <see cref="InflightExchange.LastWaitEndedAt"/> is set and the mark is gone.
/// </summary>
public sealed class InflightWaitTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(400);

    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    /// <summary>Runs <paramref name="send"/> and returns the in-flight entry seen while the exchange waited.</summary>
    private async Task<InflightExchange?> WaitingEntryDuring(Func<Task> send)
    {
        var running = send();
        InflightExchange? seen = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (seen is null && !running.IsCompleted && DateTime.UtcNow < deadline)
        {
            seen = _context.InflightRepository.Browse().FirstOrDefault(e => e.WaitingUntil is not null);
            if (seen is null)
                await Task.Delay(5);
        }
        await running;
        return seen;
    }

    private async Task SendAsync(string uri)
    {
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "x" }));
    }

    [Fact]
    public async Task OnException_redelivery_delay_is_marked_as_a_wait()
    {
        var attempts = 0;
        _context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>().MaximumRedeliveries(1).RedeliveryDelay(Wait).Handled();
            r.From("direct://redeliver").Process(_ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("first attempt");
            });
        });
        await _context.Start();

        var seen = await WaitingEntryDuring(() => SendAsync("direct://redeliver"));

        seen.Should().NotBeNull("the exchange waits for its redelivery");
        seen!.WaitingUntil!.Value.Should().BeAfter(seen.StartedAt);
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Delay_step_is_marked_as_a_wait()
    {
        _context.AddRoutes(r => r.From("direct://delayed").Delay(Wait).Process(_ => { }));
        await _context.Start();

        var seen = await WaitingEntryDuring(() => SendAsync("direct://delayed"));

        seen.Should().NotBeNull("a delay step holds the exchange by design");
    }

    [Fact]
    public async Task Retry_delay_is_marked_as_a_wait()
    {
        var attempts = 0;
        _context.AddRoutes(r => r.From("direct://retried")
            .Transaction().Retry(1, Wait)
                .Process(_ =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                        throw new InvalidOperationException("first attempt");
                })
            .EndTransaction());
        await _context.Start();

        var seen = await WaitingEntryDuring(() => SendAsync("direct://retried"));

        seen.Should().NotBeNull("the retry waits before its next attempt");
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Dead_letter_redelivery_delay_is_marked_and_ends_with_LastWaitEndedAt()
    {
        var attempts = 0;
        var inner = new LambdaProcessor(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("first attempt");
            return Task.CompletedTask;
        });
        var sut = new DeadLetterProcessor(inner, new LambdaProcessor(_ => Task.CompletedTask), RetryPolicy.Fixed(1, Wait));
        var exchange = new Exchange(new Message { Body = "x" }) { Context = _context };
        _context.InflightRepository.Register(new InflightExchange(exchange.ExchangeId, "dlc", DateTime.UtcNow, 1, null));

        var seen = await WaitingEntryDuring(() => sut.Process(exchange));

        seen.Should().NotBeNull("the dead letter channel waits before its redelivery");
        var after = _context.InflightRepository.Browse().Single();
        after.WaitingUntil.Should().BeNull("the mark is removed when the wait ends");
        after.LastWaitEndedAt.Should().NotBeNull();
    }

    [Fact]
    public void A_repository_of_ones_own_keeps_working_through_the_default_BeginWait()
    {
        IInflightRepository custom = new CountingOnlyRepository();

        using var wait = custom.BeginWait("any", Wait);

        wait.Should().NotBeNull();
    }

    private sealed class LambdaProcessor(Func<IExchange, Task> body) : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default) => body(exchange);
    }

    /// <summary>A third-party repository written before BeginWait existed.</summary>
    private sealed class CountingOnlyRepository : IInflightRepository
    {
        public void Register(InflightExchange entry) { }
        public void Unregister(string exchangeId) { }
        public IReadOnlyList<InflightExchange> Browse() => [];
        public IReadOnlyList<InflightExchange> Browse(string routeId) => [];
        public int Count => 0;
        public int CountByRoute(string routeId) => 0;
    }
}
