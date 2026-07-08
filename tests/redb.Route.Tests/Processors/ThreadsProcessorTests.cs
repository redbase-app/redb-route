using System.Collections.Concurrent;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Tests for the Threads EIP (<c>.Threads(N)</c> / <see cref="ThreadsProcessor"/>) — Camel-style
/// concurrency handoff. Covers: real concurrency up to the pool size, serial behaviour at N=1,
/// async hand-off (a serial source keeps dispatching), error routing to OnException, graceful
/// drain-on-stop (no in-flight lost), per-exchange clone isolation, and argument validation.
/// </summary>
public class ThreadsProcessorTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ── Concurrency ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Threads_SerialSource_ReachesPoolSizeConcurrency()
    {
        const int pool = 5, total = 20;
        var current = 0;
        var max = 0;
        var maxLock = new object();
        var processed = 0;

        _context.AddRoutes(r =>
        {
            r.From("direct://threads-conc")
                .Threads(pool)
                    .Process(async (e, ct) =>
                    {
                        var c = Interlocked.Increment(ref current);
                        lock (maxLock) { if (c > max) max = c; }
                        await Task.Delay(50, ct);
                        Interlocked.Decrement(ref current);
                        Interlocked.Increment(ref processed);
                    })
                .End();
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://threads-conc").CreateProducer();
        await producer.Start();

        // A serial source: each send is awaited before the next (mirrors a poll loop).
        for (var i = 0; i < total; i++)
            await producer.Process(new Exchange(new Message { Body = i }));

        await WaitForCondition(() => Volatile.Read(ref processed) >= total, TimeSpan.FromSeconds(15));

        processed.Should().Be(total, "every exchange must be processed");
        max.Should().Be(pool, "the pool runs exactly poolSize workers concurrently — never more, and it saturates");
    }

    [Fact]
    public async Task Threads_PoolSizeOne_IsSerialAndOrdered()
    {
        const int total = 10;
        var current = 0;
        var max = 0;
        var maxLock = new object();
        var received = new List<int>();

        _context.AddRoutes(r =>
        {
            r.From("direct://threads-serial")
                .Threads(1)
                    .Process(async (e, ct) =>
                    {
                        var c = Interlocked.Increment(ref current);
                        lock (maxLock) { if (c > max) max = c; }
                        await Task.Delay(5, ct);
                        lock (received) received.Add((int)e.In.Body!);
                        Interlocked.Decrement(ref current);
                    })
                .End();
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://threads-serial").CreateProducer();
        await producer.Start();

        for (var i = 0; i < total; i++)
            await producer.Process(new Exchange(new Message { Body = i }));

        await WaitForCondition(() => received.Count >= total, TimeSpan.FromSeconds(10));

        max.Should().Be(1, "poolSize 1 must never run two bodies at once");
        received.Should().HaveCount(total);
        received.Should().BeInAscendingOrder("a single worker preserves order");
    }

    [Fact]
    public async Task Threads_AsyncHandoff_ProducerReturnsBeforeBodyCompletes()
    {
        var bodyStarted = false;
        var bodyDone = false;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _context.AddRoutes(r =>
        {
            r.From("direct://threads-handoff")
                .Threads(1)
                    .Process(async (e, ct) =>
                    {
                        Volatile.Write(ref bodyStarted, true);
                        await gate.Task;
                        Volatile.Write(ref bodyDone, true);
                    })
                .End();
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://threads-handoff").CreateProducer();
        await producer.Start();

        // The send completes via the async hand-off even though the body is blocked on the gate.
        await producer.Process(new Exchange(new Message { Body = "x" }));

        await WaitForCondition(() => Volatile.Read(ref bodyStarted), TimeSpan.FromSeconds(5));
        Volatile.Read(ref bodyDone).Should().BeFalse(
            "the producer returned after hand-off while the body is still running — that is the concurrency win");

        gate.SetResult();
        await WaitForCondition(() => Volatile.Read(ref bodyDone), TimeSpan.FromSeconds(5));
    }

    // ── Error routing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Threads_BodyException_RoutedToOnException_OthersSucceed()
    {
        const int total = 8;
        var handled = new ConcurrentBag<int>();
        var succeeded = new ConcurrentBag<int>();

        _context.AddRoutes(r =>
        {
            // OnException is registered as a global handler; the detached pool worker routes to it
            // via IRouteContext.HandleException.
            r.OnException<InvalidOperationException>()
                .Handled()
                .Process(e => handled.Add((int)e.In.Body!));

            r.From("direct://threads-error")
                .Threads(3)
                    .Process(async (e, ct) =>
                    {
                        await Task.Delay(5, ct);
                        var n = (int)e.In.Body!;
                        if (n % 2 == 0) throw new InvalidOperationException($"boom-{n}");
                        succeeded.Add(n);
                    })
                .End();
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://threads-error").CreateProducer();
        await producer.Start();

        for (var i = 0; i < total; i++)
            await producer.Process(new Exchange(new Message { Body = i }));

        await WaitForCondition(() => handled.Count + succeeded.Count >= total, TimeSpan.FromSeconds(10));

        handled.Should().BeEquivalentTo(new[] { 0, 2, 4, 6 }, "even items throw and must reach OnException, not be lost");
        succeeded.Should().BeEquivalentTo(new[] { 1, 3, 5, 7 }, "odd items succeed");
    }

    // ── Drain on stop ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Threads_DrainOnStop_AllInflightProcessed_NoneLost()
    {
        const int total = 12;
        var processed = new ConcurrentBag<int>();

        _context.AddRoutes(r =>
        {
            r.From("direct://threads-drain")
                .Threads(3)
                    .Process(async (e, ct) =>
                    {
                        await Task.Delay(60, ct);
                        processed.Add((int)e.In.Body!);
                    })
                .End();
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://threads-drain").CreateProducer();
        await producer.Start();

        for (var i = 0; i < total; i++)
            await producer.Process(new Exchange(new Message { Body = i }));

        // Stop immediately — the pool must drain every in-flight/queued exchange, losing none.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _context.Stop(cts.Token);

        processed.Should().HaveCount(total, "graceful drain-on-stop must not drop any in-flight exchange");
    }

    // ── Processor-level: clone isolation + validation ─────────────────────────────

    [Fact]
    public async Task Threads_ClonesExchangePerHandoff_BodyGetsDistinctInstance()
    {
        IExchange? seenByBody = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var body = new DelegateProcessor(e =>
        {
            seenByBody = e;
            done.TrySetResult();
        });

        var processor = new ThreadsProcessor(body, poolSize: 1, maxQueueSize: 0, _context);

        var original = new Exchange(new Message { Body = "orig" });
        await processor.Process(original);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        seenByBody.Should().NotBeNull();
        ReferenceEquals(seenByBody, original).Should().BeFalse(
            "each hand-off clones the exchange so the worker never shares the caller's mutable exchange/scope");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Threads_InvalidPoolSize_Throws(int poolSize)
    {
        var body = new DelegateProcessor(_ => { });
        var act = () => new ThreadsProcessor(body, poolSize, maxQueueSize: 0, _context);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── InOut / RPC (adaptive by pattern — inline gate on the SAME exchange) ───────

    [Fact]
    public async Task Threads_InOut_ReplyOnOut_Preserved()
    {
        // InOut runs the body inline on the SAME exchange — a reply written to Out is simply there.
        var body = new DelegateProcessor(e =>
        {
            e.Out ??= e.In.Clone();
            e.Out.Body = $"reply:{e.In.Body}";
        });
        var processor = new ThreadsProcessor(body, poolSize: 2, maxQueueSize: 0, _context);

        var ex = new Exchange(new Message { Body = "ping" }) { Pattern = ExchangePattern.InOut };
        await processor.Process(ex);

        ex.Out.Should().NotBeNull("InOut must preserve the reply across the threads gate");
        ex.Out!.Body.Should().Be("reply:ping");
    }

    [Fact]
    public async Task Threads_InOut_ReplyOnIn_Preserved()
    {
        // Many redb.Route routes (e.g. the HTTP consumer's `HasOut ? Out : In` fallback) write the
        // response into In, not Out. The inline gate must preserve that too — nothing is cloned/copied.
        var body = new DelegateProcessor(e =>
        {
            e.In.Body = $"in-reply:{e.In.Body}";
            e.In.Headers["redbHttp.ResponseCode"] = 404;
        });
        var processor = new ThreadsProcessor(body, poolSize: 2, maxQueueSize: 0, _context);

        var ex = new Exchange(new Message { Body = "ping" }) { Pattern = ExchangePattern.InOut };
        await processor.Process(ex);

        ex.HasOut.Should().BeFalse("the body wrote to In, not Out");
        ex.In.Body.Should().Be("in-reply:ping", "an In-based response must survive the gate");
        ex.In.Headers["redbHttp.ResponseCode"].Should().Be(404, "In headers (status code) must survive too");
    }

    [Fact]
    public async Task Threads_InOut_UnhandledBodyException_PropagatesToCaller()
    {
        // Inline gate: the fault propagates straight up to the awaiting caller (no OnException here).
        var body = new DelegateProcessor(_ => throw new InvalidOperationException("boom"));
        var processor = new ThreadsProcessor(body, poolSize: 1, maxQueueSize: 0, _context);

        var ex = new Exchange(new Message { Body = "x" }) { Pattern = ExchangePattern.InOut };
        var act = async () => await processor.Process(ex);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task Threads_InOut_HandledException_DoesNotThrow_AndReplyFlowsBack()
    {
        // Inline: the fault propagates up to the route's outer OnException wrapper exactly as an
        // un-threaded route. A handled exception must NOT surface to the caller; the recovery flows back.
        _context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>()
                .Handled()
                .Process(e => { e.Out ??= e.In.Clone(); e.Out.Body = "recovered"; });

            r.From("direct://threads-inout-handled")
                .Threads(2)
                    .Process((IExchange _, CancellationToken _) => throw new InvalidOperationException("boom"))
                .End();
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://threads-inout-handled").CreateProducer();
        await producer.Start();

        var ex = new Exchange(new Message { Body = "x" }) { Pattern = ExchangePattern.InOut };
        var act = async () => await producer.Process(ex);

        await act.Should().NotThrowAsync("a handled exception must not surface to the InOut caller");
        ex.Out.Should().NotBeNull();
        ex.Out!.Body.Should().Be("recovered", "the OnException recovery reply flows back to the caller");
    }

    [Fact]
    public async Task Threads_InOut_GateCapsConcurrencyAtPoolSize()
    {
        // The gate must cap concurrent InOut bodies at poolSize even when many requests arrive at once.
        const int pool = 3, total = 12;
        var current = 0;
        var max = 0;
        var maxLock = new object();

        var body = new DelegateProcessor(async (_, ct) =>
        {
            var c = Interlocked.Increment(ref current);
            lock (maxLock) { if (c > max) max = c; }
            await Task.Delay(40, ct);
            Interlocked.Decrement(ref current);
        });
        var processor = new ThreadsProcessor(body, poolSize: pool, maxQueueSize: 0, _context);

        var calls = Enumerable.Range(0, total).Select(i =>
            processor.Process(new Exchange(new Message { Body = i }) { Pattern = ExchangePattern.InOut }));
        await Task.WhenAll(calls);

        max.Should().Be(pool, "the InOut gate runs exactly poolSize bodies concurrently — never more, and it saturates");
    }

    [Fact]
    public async Task Threads_InOut_GateTimeout_ThrowsWhenSaturated()
    {
        // poolSize 1: the first InOut takes the only permit and blocks in its body; a second InOut can't
        // acquire a permit within EnqueueTimeout and fails fast with TimeoutException.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new DelegateProcessor(async (_, _) => await release.Task);
        var processor = new ThreadsProcessor(body, poolSize: 1, maxQueueSize: 0, _context)
        {
            EnqueueTimeout = TimeSpan.FromMilliseconds(200)
        };

        // Fire the first InOut WITHOUT awaiting — it holds the single permit, blocked on `release`.
        var first = processor.Process(new Exchange(new Message { Body = 1 }) { Pattern = ExchangePattern.InOut });
        try
        {
            await Task.Delay(50); // ensure the permit is taken

            var act = async () => await processor.Process(
                new Exchange(new Message { Body = 2 }) { Pattern = ExchangePattern.InOut });
            await act.Should().ThrowAsync<TimeoutException>("the gate is full ⇒ no permit within the timeout");
        }
        finally
        {
            release.SetResult();
            await first;
        }
    }

    [Fact]
    public async Task Threads_EnqueueTimeout_ThrowsWhenPoolSaturated()
    {
        // InOnly path: poolSize 1 + queue 1: worker takes #1 and blocks, #2 fills the single slot, #3
        // finds no free slot within EnqueueTimeout and fails fast instead of waiting indefinitely.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new DelegateProcessor(async (_, _) => await release.Task);
        var processor = new ThreadsProcessor(body, poolSize: 1, maxQueueSize: 1, _context)
        {
            EnqueueTimeout = TimeSpan.FromMilliseconds(200)
        };

        try
        {
            await processor.Process(new Exchange(new Message { Body = 1 })); // worker picks up, blocks
            await processor.Process(new Exchange(new Message { Body = 2 })); // fills the queue slot
            await Task.Delay(50);                                           // let #1 be dequeued + blocked

            var act = async () => await processor.Process(new Exchange(new Message { Body = 3 }));
            await act.Should().ThrowAsync<TimeoutException>("pool busy + queue full ⇒ no slot within the timeout");
        }
        finally
        {
            release.SetResult();
        }
    }

    // ── Helper ────────────────────────────────────────────────────────────────────

    private static async Task WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}
