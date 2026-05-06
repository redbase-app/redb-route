using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Tests for exception propagation, thread safety, and resource disposal in processors.
/// </summary>
public class ExceptionAndThreadSafetyTests
{
    // ══════════════════════════════════════════════════════════════
    // LogicalExpression — throws on invalid expression
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void LogicalExpression_InvalidExpression_ThrowsEvaluationException()
    {
        // An expression referencing a non-existent property in a way that breaks evaluation
        var expr = new LogicalExpression("~~~INVALID~~~");
        var exchange = new Exchange(new Message("body"));

        var act = () => expr.Evaluate<bool>(exchange);

        act.Should().Throw<ExpressionEvaluationException>()
            .WithMessage("*~~~INVALID~~~*");
    }

    [Fact]
    public void LogicalExpression_ValidExpression_StillWorks()
    {
        var expr = new LogicalExpression("property.x > 0");
        var exchange = new Exchange(new Message("body"));
        exchange.Properties["x"] = 5;

        expr.Evaluate<bool>(exchange).Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════
    // DebounceProcessor — downstream errors are logged and recorded
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Debounce_DownstreamThrows_SetsExchangeException()
    {
        var expected = new InvalidOperationException("boom");
        IExchange? capturedExchange = null;

        var failingNext = new DelegateProcessor(ex =>
        {
            capturedExchange = ex;
            throw expected;
        });

        var logger = Substitute.For<ILogger>();
        var processor = new DebounceProcessor(
            failingNext,
            e => e.In.Headers.TryGetValue("key", out var v) ? v?.ToString() ?? "" : "",
            TimeSpan.FromMilliseconds(50),
            logger);

        var exchange = Exchange.Create(new Message("data"), null);
        exchange.In.Headers["key"] = "k1";

        await processor.Process(exchange);

        // Wait for the debounce timer to fire and the downstream to fail
        await Task.Delay(200);

        capturedExchange.Should().NotBeNull();
        capturedExchange!.Exception.Should().BeSameAs(expected);

        // Logger should have received an error call
        logger.ReceivedCalls().Should().NotBeEmpty();

        await processor.DisposeAsync();
    }

    [Fact]
    public async Task Debounce_FlushAsync_DownstreamThrows_SetsExchangeException()
    {
        var expected = new InvalidOperationException("flush-boom");
        IExchange? capturedExchange = null;

        var failingNext = new DelegateProcessor(ex =>
        {
            capturedExchange = ex;
            throw expected;
        });

        var logger = Substitute.For<ILogger>();
        var processor = new DebounceProcessor(
            failingNext,
            e => e.In.Headers.TryGetValue("key", out var v) ? v?.ToString() ?? "" : "",
            TimeSpan.FromHours(1), // Long quiet period — won't fire by itself
            logger);

        var exchange = Exchange.Create(new Message("data"), null);
        exchange.In.Headers["key"] = "k1";

        await processor.Process(exchange);

        // Force flush instead of waiting for timer
        await processor.FlushAsync();

        capturedExchange.Should().NotBeNull();
        capturedExchange!.Exception.Should().BeSameAs(expected);
        logger.ReceivedCalls().Should().NotBeEmpty();

        processor.Dispose();
    }

    // ══════════════════════════════════════════════════════════════
    // DynamicRouterProcessor — thread safety under concurrent access
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DynamicRouter_ConcurrentAccess_NoCorruption()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(_ => Substitute.For<IProducer>());
        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint(Arg.Any<string>()).Returns(endpoint);

        var exceptions = new ConcurrentBag<Exception>();

        // 10 concurrent tasks each routing to different URIs
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            try
            {
                var router = new DynamicRouterProcessor(context, ex =>
                {
                    var body = ex.In.Body?.ToString();
                    return body == "done" ? null : $"direct://target-{i}";
                });

                var exchange = new Exchange(new Message("go"));
                // After first hop, mark as done so it stops
                var hop = 0;
                var drRouter = new DynamicRouterProcessor(context, _ =>
                {
                    hop++;
                    return hop <= 1 ? $"direct://t{i}" : null;
                });

                await drRouter.Process(new Exchange(new Message("data")));
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("concurrent access should not cause corruption");
    }

    // ══════════════════════════════════════════════════════════════
    // DynamicRouterProcessor — DisposeAsync stops cached producers
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DynamicRouter_DisposeAsync_StopsAllProducers()
    {
        var producerA = Substitute.For<IProducer>();
        var producerB = Substitute.For<IProducer>();

        var endpointA = Substitute.For<IEndpoint>();
        endpointA.CreateProducer().Returns(producerA);
        var endpointB = Substitute.For<IEndpoint>();
        endpointB.CreateProducer().Returns(producerB);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("direct://a").Returns(endpointA);
        context.GetEndpoint("direct://b").Returns(endpointB);

        var hops = new Queue<string?>(["direct://a", "direct://b", null]);
        var router = new DynamicRouterProcessor(context, _ => hops.Dequeue());
        await router.Process(new Exchange(new Message("data")));

        await router.DisposeAsync();

        await producerA.Received(1).Stop(Arg.Any<CancellationToken>());
        await producerB.Received(1).Stop(Arg.Any<CancellationToken>());
    }

    // ══════════════════════════════════════════════════════════════
    // RecipientListProcessor — DisposeAsync stops cached producers
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RecipientList_DisposeAsync_StopsAllProducers()
    {
        var producerA = Substitute.For<IProducer>();
        var producerB = Substitute.For<IProducer>();

        var endpointA = Substitute.For<IEndpoint>();
        endpointA.CreateProducer().Returns(producerA);
        var endpointB = Substitute.For<IEndpoint>();
        endpointB.CreateProducer().Returns(producerB);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("seda://a").Returns(endpointA);
        context.GetEndpoint("seda://b").Returns(endpointB);

        var processor = new RecipientListProcessor(
            context,
            _ => new[] { "seda://a", "seda://b" });

        await processor.Process(new Exchange(new Message("data")));

        await processor.DisposeAsync();

        await producerA.Received(1).Stop(Arg.Any<CancellationToken>());
        await producerB.Received(1).Stop(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecipientList_DisposeAsync_IdempotentAfterDispose()
    {
        var producer = Substitute.For<IProducer>();
        var endpoint = Substitute.For<IEndpoint>();
        endpoint.CreateProducer().Returns(producer);

        var context = Substitute.For<IRouteContext>();
        context.GetEndpoint("seda://x").Returns(endpoint);

        var processor = new RecipientListProcessor(context, _ => new[] { "seda://x" });
        await processor.Process(new Exchange(new Message("data")));

        await processor.DisposeAsync();
        await processor.DisposeAsync(); // Second call should be safe

        await producer.Received(1).Stop(Arg.Any<CancellationToken>());
    }
}
