using System.Threading.Channels;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Components;

/// <summary>
/// In-memory queues of a route context across <c>Stop()</c> and <c>Start()</c> of the same instance (redb-route issue
/// #12): the context keeps its components and endpoints, so a queue that the stop closed for good left every hop
/// behind it dead while the routes reported Started.
/// </summary>
public sealed class ContextRestartQueueTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // IAsyncLifetime, not IAsyncDisposable: xUnit 2 never calls DisposeAsync of IAsyncDisposable on a test class.
    public async Task DisposeAsync() => await _context.DisposeAsync();

    private async Task SendAsync(string uri, object body)
    {
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = body }));
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        condition().Should().BeTrue(because);
    }

    [Fact]
    public async Task Seda_delivers_after_the_context_is_stopped_and_started_again()
    {
        var received = 0;
        _context.AddRoutes(r => r.From("direct://input").To("seda://queue"));
        _context.AddRoutes(r => r.From("seda://queue").Process(_ => Interlocked.Increment(ref received)));

        await _context.Start();
        await SendAsync("direct://input", "first");
        await WaitForAsync(() => Volatile.Read(ref received) == 1, "до остановки seda доставляет");

        await _context.Stop();
        await _context.Start();
        await SendAsync("direct://input", "second");

        await WaitForAsync(() => Volatile.Read(ref received) == 2,
            "после Stop и Start того же контекста очередь seda снова доставляет");
    }

    [Fact]
    public async Task Seda_stop_finishes_the_queue_and_the_start_opens_a_new_one()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new List<string>();
        _context.AddRoutes(r => r.From("seda://waiting").Process(async (IExchange e, CancellationToken _) =>
        {
            await gate.Task;
            lock (processed) processed.Add((string)e.In.Body!);
        }));
        await _context.Start();

        // The first exchange is in the route when the context stops; the other two wait in the queue.
        foreach (var body in new[] { "a", "b", "c" })
            await SendAsync("seda://waiting", body);
        var stopping = _context.Stop();
        gate.SetResult();
        await stopping;
        lock (processed) processed.Should().Equal(["a", "b", "c"], "остановка дорабатывает то, что уже в очереди");

        var whileStopped = () => SendAsync("seda://waiting", "refused");
        await whileStopped.Should().ThrowAsync<ChannelClosedException>("остановленная очередь отказывает видимо");

        await _context.Start();
        await SendAsync("seda://waiting", "d");

        await WaitForAsync(() => { lock (processed) return processed.Count == 4; },
            "после старта очередь новая и принимает");
        lock (processed) processed.Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public async Task Seda_refuses_a_send_once_the_context_is_disposed()
    {
        _context.AddRoutes(r => r.From("seda://closed").Process(_ => { }));
        await _context.Start();
        var producer = _context.GetEndpoint("seda://closed").CreateProducer();
        await producer.Start();

        await _context.DisposeAsync();

        var send = () => producer.Process(new Exchange(new Message { Body = "late" }));
        await send.Should().ThrowAsync<ChannelClosedException>("отправка в очередь закрытого контекста видна, а не пропадает");
    }

    [Fact]
    public async Task Threads_delivers_after_the_context_is_stopped_and_started_again()
    {
        var received = 0;
        _context.AddRoutes(r => r.From("direct://pooled")
            .Threads(2)
                .Process(_ => Interlocked.Increment(ref received))
            .End());

        await _context.Start();
        await SendAsync("direct://pooled", "first");
        await WaitForAsync(() => Volatile.Read(ref received) == 1, "до остановки пул Threads обрабатывает");

        await _context.Stop();
        await _context.Start();
        await SendAsync("direct://pooled", "second");

        await WaitForAsync(() => Volatile.Read(ref received) == 2,
            "после Stop и Start того же контекста пул Threads снова обрабатывает");
    }
}
