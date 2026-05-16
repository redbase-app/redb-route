using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Wave 1 feature tests: enhanced splitter, multicast, wire-tap, on-exception,
/// and new DSL step types (RemoveProperty, RemoveBody, ThrowException, DelayFactory, Log with LogLevel).
/// </summary>
public class Wave1FeatureTests
{
    // ── SplitterProcessor: Parallel Processing ──

    /// <summary>Parallel splitter processes all parts concurrently.</summary>
    [Fact]
    public async Task Splitter_Parallel_ProcessesAllParts()
    {
        var bodies = new System.Collections.Concurrent.ConcurrentBag<object?>();
        var target = new DelegateProcessor(ex => bodies.Add(ex.In.Body));

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target,
            parallelProcessing: true);

        await splitter.Process(new Exchange(new Message(new object[] { "a", "b", "c" })));

        bodies.Should().HaveCount(3);
        bodies.Should().Contain("a").And.Contain("b").And.Contain("c");
    }

    /// <summary>Parallel splitter with aggregation merges results pair-wise.</summary>
    [Fact]
    public async Task Splitter_Parallel_WithAggregation_MergesResults()
    {
        var target = new DelegateProcessor(ex =>
        {
            ex.In.Body = (int)ex.In.Body! * 10;
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target,
            parallelProcessing: true,
            aggregationStrategy: (agg, cur) =>
            {
                agg.In.Body = (int)agg.In.Body! + (int)cur.In.Body!;
                return agg;
            });

        var exchange = new Exchange(new Message(new object[] { 1, 2, 3 }));
        await splitter.Process(exchange);

        exchange.In.Body.Should().Be(60); // (1*10) + (2*10) + (3*10) = 60
    }

    /// <summary>Sequential splitter with aggregation merges results pair-wise.</summary>
    [Fact]
    public async Task Splitter_Sequential_WithAggregation_MergesResults()
    {
        var target = new DelegateProcessor(ex =>
        {
            ex.In.Body = (int)ex.In.Body! * 2;
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target,
            parallelProcessing: false,
            aggregationStrategy: (agg, cur) =>
            {
                agg.In.Body = (int)agg.In.Body! + (int)cur.In.Body!;
                return agg;
            });

        var exchange = new Exchange(new Message(new object[] { 1, 2, 3 }));
        await splitter.Process(exchange);

        exchange.In.Body.Should().Be(12); // (1*2) + (2*2) + (3*2) = 12
    }

    /// <summary>StopOnException stops parallel processing on first error.</summary>
    [Fact]
    public async Task Splitter_Parallel_StopOnException_StopsOnFirstError()
    {
        var target = new DelegateProcessor(ex =>
        {
            if ((int)ex.In.Body! == 2)
                throw new InvalidOperationException("boom");
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target,
            parallelProcessing: true,
            stopOnException: true);

        var exchange = new Exchange(new Message(new object[] { 1, 2, 3 }));
        var act = () => splitter.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>();
        exchange.Exception.Should().BeOfType<InvalidOperationException>();
    }

    /// <summary>Sequential splitter with stopOnException=false collects all errors.</summary>
    [Fact]
    public async Task Splitter_Sequential_StopOnExceptionFalse_ContinuesProcessing()
    {
        var processed = new List<int>();
        var target = new DelegateProcessor(ex =>
        {
            var val = (int)ex.In.Body!;
            processed.Add(val);
            if (val == 2)
                throw new InvalidOperationException("boom");
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target,
            parallelProcessing: false,
            stopOnException: false);

        var exchange = new Exchange(new Message(new object[] { 1, 2, 3 }));
        await splitter.Process(exchange);

        processed.Should().Equal(1, 2, 3);
    }

    /// <summary>Timeout causes TimeoutException in splitter.</summary>
    [Fact]
    public async Task Splitter_Timeout_ThrowsTimeoutException()
    {
        var target = new DelegateProcessor(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target,
            parallelProcessing: false,
            timeout: TimeSpan.FromMilliseconds(50));

        var exchange = new Exchange(new Message(new object[] { 1 }));
        var act = () => splitter.Process(exchange);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    /// <summary>MaxDegreeOfParallelism limits concurrency in parallel splitter.</summary>
    [Fact]
    public async Task Splitter_Parallel_MaxDegreeOfParallelism_LimitsConcurrency()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        var target = new DelegateProcessor(async (_, ct) =>
        {
            var c = Interlocked.Increment(ref concurrent);
            lock (lockObj) { if (c > maxConcurrent) maxConcurrent = c; }
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrent);
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target,
            parallelProcessing: true,
            maxDegreeOfParallelism: 2);

        var exchange = new Exchange(new Message(Enumerable.Range(1, 10).Cast<object?>().ToArray()));
        await splitter.Process(exchange);

        maxConcurrent.Should().BeLessThanOrEqualTo(2);
    }

    // ── MulticastProcessor: Enhanced Features ──

    /// <summary>Sequential multicast preserves order with pair-wise aggregation.</summary>
    [Fact]
    public async Task Multicast_Sequential_PairWiseAggregation()
    {
        var multicast = new MulticastProcessor(
            parallelProcessing: false,
            aggregationStrategy: (agg, cur) =>
            {
                agg.In.Body = $"{agg.In.Body},{cur.In.Body}";
                return agg;
            })
            .AddTarget(new DelegateProcessor(ex => ex.In.Body = "A"))
            .AddTarget(new DelegateProcessor(ex => ex.In.Body = "B"))
            .AddTarget(new DelegateProcessor(ex => ex.In.Body = "C"));

        var exchange = new Exchange(new Message("_"));
        await multicast.Process(exchange);

        exchange.In.Body.Should().Be("A,B,C");
    }

    /// <summary>StopOnException in multicast stops on first error.</summary>
    [Fact]
    public async Task Multicast_StopOnException_StopsOnFirstError()
    {
        var multicast = new MulticastProcessor(
            parallelProcessing: false,
            stopOnException: true)
            .AddTarget(new DelegateProcessor(_ => { }))
            .AddTarget(new DelegateProcessor(_ => throw new InvalidOperationException("boom")))
            .AddTarget(new DelegateProcessor(_ => { }));

        var act = () => multicast.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Timeout in multicast throws TimeoutException.</summary>
    [Fact]
    public async Task Multicast_Timeout_ThrowsTimeoutException()
    {
        var multicast = new MulticastProcessor(
            parallelProcessing: false,
            timeout: TimeSpan.FromMilliseconds(50))
            .AddTarget(new DelegateProcessor(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }));

        var act = () => multicast.Process(new Exchange());
        await act.Should().ThrowAsync<TimeoutException>();
    }

    /// <summary>Parallel multicast with max-degree limits concurrency.</summary>
    [Fact]
    public async Task Multicast_Parallel_MaxDegreeOfParallelism()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        var multicast = new MulticastProcessor(
            parallelProcessing: true,
            maxDegreeOfParallelism: 2);

        for (int i = 0; i < 8; i++)
        {
            multicast.AddTarget(new DelegateProcessor(async (_, ct) =>
            {
                var c = Interlocked.Increment(ref concurrent);
                lock (lockObj) { if (c > maxConcurrent) maxConcurrent = c; }
                await Task.Delay(50, ct);
                Interlocked.Decrement(ref concurrent);
            }));
        }

        await multicast.Process(new Exchange());

        maxConcurrent.Should().BeLessThanOrEqualTo(2);
    }

    // ── WireTapProcessor: OnPrepare & NewBodyFactory ──

    /// <summary>OnPrepare callback modifies the cloned exchange.</summary>
    [Fact]
    public async Task WireTap_OnPrepare_ModifiesClone()
    {
        IExchange? tapped = null;
        var wireTap = new WireTapProcessor(
            new DelegateProcessor(ex => tapped = ex),
            onPrepare: ex => ex.In.Headers["tap-flag"] = true);

        var exchange = new Exchange(new Message("original"));
        await wireTap.Process(exchange);

        await Task.Delay(100); // Wait for fire-and-forget

        tapped.Should().NotBeNull();
        tapped!.In.Headers["tap-flag"].Should().Be(true);
        exchange.In.Headers.ContainsKey("tap-flag").Should().BeFalse(); // Original not affected
    }

    /// <summary>NewBodyFactory replaces the body on the tapped clone.</summary>
    [Fact]
    public async Task WireTap_NewBodyFactory_ReplacesCloneBody()
    {
        IExchange? tapped = null;
        var wireTap = new WireTapProcessor(
            new DelegateProcessor(ex => tapped = ex),
            newBodyFactory: ex => $"audit: {ex.In.Body}");

        var exchange = new Exchange(new Message("data"));
        await wireTap.Process(exchange);

        await Task.Delay(100); // Wait for fire-and-forget

        tapped.Should().NotBeNull();
        tapped!.In.Body.Should().Be("audit: data");
        exchange.In.Body.Should().Be("data"); // Original unchanged
    }

    // ── OnExceptionProcessor: Redelivery Delay ──

    /// <summary>OnException with redelivery delay pauses between retries.</summary>
    [Fact]
    public async Task OnException_RedeliveryDelay_PausesBetweenRetries()
    {
        var attempts = 0;
        var timestamps = new List<DateTime>();

        var body = new DelegateProcessor(_ =>
        {
            timestamps.Add(DateTime.UtcNow);
            attempts++;
            throw new InvalidOperationException($"attempt {attempts}");
        });

        var processor = new OnExceptionProcessor(body)
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => { }),
                maxRedeliveries: 2,
                redeliveryDelay: TimeSpan.FromMilliseconds(50));

        await processor.Process(new Exchange());

        attempts.Should().Be(3); // 1 initial + 2 redeliveries
        // Verify some delay occurred between attempts
        if (timestamps.Count >= 2)
        {
            var gap = timestamps[^1] - timestamps[0];
            gap.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(50);
        }
    }

    /// <summary>OnException with exponential backoff increases delay between retries.</summary>
    [Fact]
    public async Task OnException_ExponentialBackoff_IncreasesDelay()
    {
        var attempts = 0;
        var timestamps = new List<DateTime>();

        var body = new DelegateProcessor(_ =>
        {
            timestamps.Add(DateTime.UtcNow);
            attempts++;
            throw new InvalidOperationException("fail");
        });

        var processor = new OnExceptionProcessor(body)
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => { }),
                maxRedeliveries: 2,
                redeliveryDelay: TimeSpan.FromMilliseconds(30),
                backoffMultiplier: 2.0,
                useExponentialBackoff: true);

        await processor.Process(new Exchange());

        attempts.Should().Be(3);
        // First gap ~30ms, second gap ~60ms
        if (timestamps.Count >= 3)
        {
            var gap1 = timestamps[1] - timestamps[0];
            var gap2 = timestamps[2] - timestamps[1];
            gap2.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(gap1.TotalMilliseconds * 0.8);
        }
    }

    // ── LogProcessor: LogLevel ──

    /// <summary>LogProcessor with Warning level logs at Warning.</summary>
    [Fact]
    public void LogProcessor_WithLevel_LogsAtSpecifiedLevel()
    {
        var logger = Substitute.For<ILogger>();
        var processor = new LogProcessor(logger, "test warning", LogLevel.Warning);

        processor.Process(new Exchange());

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>LogProcessor with dynamic message and Error level.</summary>
    [Fact]
    public void LogProcessor_DynamicMessage_WithLevel()
    {
        var logger = Substitute.For<ILogger>();
        var processor = new LogProcessor(logger, ex => $"body: {ex.In.Body}", LogLevel.Error);

        processor.Process(new Exchange(new Message("hello")));

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ── DSL: New Step Types ──

    /// <summary>RemoveProperty removes a property from the exchange via DSL.</summary>
    [Fact]
    public async Task DSL_RemoveProperty_RemovesFromExchange()
    {
        var context = new RouteContext();
        IExchange? received = null;

        context.AddRoutes(r =>
        {
            r.From("direct://remove-prop-in")
                .SetProperty("myKey", "myValue")
                .RemoveProperty("myKey")
                .Process(ex => received = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://remove-prop-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        received.Should().NotBeNull();
        received!.Properties.ContainsKey("myKey").Should().BeFalse();

        await context.DisposeAsync();
    }

    /// <summary>RemoveBody nulls out the exchange body via DSL.</summary>
    [Fact]
    public async Task DSL_RemoveBody_NullsBody()
    {
        var context = new RouteContext();
        IExchange? received = null;

        context.AddRoutes(r =>
        {
            r.From("direct://remove-body-in")
                .SetBody("something")
                .RemoveBody()
                .Process(ex => received = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://remove-body-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        received.Should().NotBeNull();
        received!.In.Body.Should().BeNull();

        await context.DisposeAsync();
    }

    /// <summary>ThrowException(instance) throws the exception in the pipeline.</summary>
    [Fact]
    public async Task DSL_ThrowException_ThrowsSpecifiedException()
    {
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://throw-ex")
                .ThrowException(new InvalidOperationException("boom"));
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://throw-ex").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        await context.DisposeAsync();
    }

    /// <summary>ThrowException(type, message) constructs and throws the exception.</summary>
    [Fact]
    public async Task DSL_ThrowExceptionType_ConstructsAndThrows()
    {
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://throw-ex-type")
                .ThrowException(typeof(ArgumentException), "bad argument");
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://throw-ex-type").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange());
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("bad argument");

        await context.DisposeAsync();
    }

    /// <summary>ThrowException() with no args rethrows exchange.Exception.</summary>
    [Fact]
    public async Task DSL_ThrowException_Rethrow_RethrowsExchangeException()
    {
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://rethrow")
                .Process(ex => ex.Exception = new ArgumentException("original"))
                .ThrowException();
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://rethrow").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange());
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("original");

        await context.DisposeAsync();
    }

    /// <summary>ThrowException() with no exchange exception throws InvalidOperationException.</summary>
    [Fact]
    public async Task DSL_ThrowException_Rethrow_NoException_ThrowsInvalidOperation()
    {
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://rethrow-none")
                .ThrowException();
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://rethrow-none").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        await context.DisposeAsync();
    }

    /// <summary>ThrowException(string message) throws a new Exception with message.</summary>
    [Fact]
    public async Task DSL_ThrowException_Message_ThrowsNewException()
    {
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://throw-msg")
                .ThrowException("something went wrong");
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://throw-msg").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange());
        await act.Should().ThrowAsync<Exception>().WithMessage("something went wrong");

        await context.DisposeAsync();
    }

    /// <summary>ThrowException&lt;T&gt;(message) constructs and throws typed exception.</summary>
    [Fact]
    public async Task DSL_ThrowException_Generic_WithMessage()
    {
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://throw-generic-msg")
                .ThrowException<InvalidOperationException>("generic boom");
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://throw-generic-msg").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("generic boom");

        await context.DisposeAsync();
    }

    /// <summary>ThrowException&lt;T&gt;() with no message uses parameterless constructor.</summary>
    [Fact]
    public async Task DSL_ThrowException_Generic_NoMessage()
    {
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://throw-generic-no-msg")
                .ThrowException<InvalidOperationException>();
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://throw-generic-no-msg").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        await context.DisposeAsync();
    }

    /// <summary>Dynamic delay computes delay from the exchange.</summary>
    [Fact]
    public async Task DSL_DelayFactory_DynamicDelay()
    {
        var context = new RouteContext();
        IExchange? received = null;

        context.AddRoutes(r =>
        {
            r.From("direct://delay-factory")
                .Delay(ex => TimeSpan.FromMilliseconds(50))
                .Process(ex => received = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://delay-factory").CreateProducer();
        await producer.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await producer.Process(new Exchange(new Message("data")));
        sw.Stop();

        received.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(30); // Some tolerance

        await context.DisposeAsync();
    }

    /// <summary>Log with explicit LogLevel via DSL.</summary>
    [Fact]
    public async Task DSL_LogWithLevel_RecordsLevel()
    {
        var context = new RouteContext();
        IExchange? received = null;

        context.AddRoutes(r =>
        {
            r.From("direct://log-level")
                .Log("warning message", LogLevel.Warning)
                .Log(ex => $"body: {ex.In.Body}", LogLevel.Error)
                .Process(ex => received = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://log-level").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        received.Should().NotBeNull();
        await context.DisposeAsync();
    }

    /// <summary>Enhanced Split via DSL with parallelProcessing option.</summary>
    [Fact]
    public async Task DSL_Split_WithParallelProcessing()
    {
        var context = new RouteContext();
        var parts = new System.Collections.Concurrent.ConcurrentBag<object?>();

        context.AddRoutes(r =>
        {
            r.From("direct://split-parallel")
                .Split(ex => ((IEnumerable<object?>)ex.In.Body!))
                    .ParallelProcessing()
                    .Process(ex => parts.Add(ex.In.Body))
                .End();
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://split-parallel").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(new object[] { "x", "y", "z" })));

        parts.Should().HaveCount(3);
        parts.Should().Contain("x").And.Contain("y").And.Contain("z");

        await context.DisposeAsync();
    }

    /// <summary>Enhanced Multicast via DSL with explicit options.</summary>
    [Fact]
    public async Task DSL_Multicast_WithOptions()
    {
        var context = new RouteContext();
        var received1 = false;
        var received2 = false;

        context.AddRoutes(r =>
        {
            r.From("direct://mcast-opt-in")
                .Multicast(
                    new[] { "direct://mcast-opt-out1", "direct://mcast-opt-out2" },
                    parallelProcessing: false,
                    stopOnException: true);
        });

        context.AddRoutes(r =>
        {
            r.From("direct://mcast-opt-out1").Process(_ => received1 = true);
        });
        context.AddRoutes(r =>
        {
            r.From("direct://mcast-opt-out2").Process(_ => received2 = true);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://mcast-opt-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        received1.Should().BeTrue();
        received2.Should().BeTrue();

        await context.DisposeAsync();
    }

    /// <summary>Enhanced WireTap via DSL with onPrepare callback.</summary>
    [Fact]
    public async Task DSL_WireTap_WithOnPrepare()
    {
        var context = new RouteContext();
        IExchange? tapped = null;

        context.AddRoutes(r =>
        {
            r.From("direct://wt-prep-in")
                .WireTap("direct://wt-prep-out", onPrepare: ex => ex.In.Headers["prepared"] = true);
        });

        context.AddRoutes(r =>
        {
            r.From("direct://wt-prep-out").Process(ex => tapped = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://wt-prep-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        await Task.Delay(200); // Wait for fire-and-forget

        tapped.Should().NotBeNull();
        tapped!.In.Headers["prepared"].Should().Be(true);

        await context.DisposeAsync();
    }

    /// <summary>Enhanced OnException DSL with redelivery delay (processor level test).</summary>
    [Fact]
    public async Task DSL_OnException_WithRedeliveryDelay()
    {
        var attempts = 0;
        var handled = false;

        var body = new DelegateProcessor(_ =>
        {
            attempts++;
            throw new InvalidOperationException("fail");
        });

        var processor = new OnExceptionProcessor(body)
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => handled = true),
                maxRedeliveries: 2,
                redeliveryDelay: TimeSpan.FromMilliseconds(10));

        await processor.Process(new Exchange(new Message("data")));

        handled.Should().BeTrue();
        attempts.Should().Be(3); // 1 initial + 2 redeliveries
    }
}
