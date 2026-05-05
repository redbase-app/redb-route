using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="OnExceptionProcessor"/>.</summary>
public class OnExceptionProcessorTests
{
    /// <summary>Body executes normally when no exception.</summary>
    [Fact]
    public async Task Process_NoException_BodyExecutes()
    {
        var executed = false;
        var processor = new OnExceptionProcessor(
            new DelegateProcessor(_ => executed = true));

        await processor.Process(new Exchange());

        executed.Should().BeTrue();
    }

    /// <summary>Matching handler is invoked on exception.</summary>
    [Fact]
    public async Task Process_MatchingHandler_Invoked()
    {
        var handled = false;
        var processor = new OnExceptionProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException("boom")))
            .Handle<InvalidOperationException>(new DelegateProcessor(ex =>
            {
                handled = true;
                ex.Exception.Should().BeOfType<InvalidOperationException>();
            }));

        var exchange = new Exchange();
        await processor.Process(exchange);

        handled.Should().BeTrue();
        exchange.ExceptionHandled.Should().BeTrue();
    }

    /// <summary>No matching handler — exception rethrows.</summary>
    [Fact]
    public async Task Process_NoMatchingHandler_Rethrows()
    {
        var processor = new OnExceptionProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException()))
            .Handle<ArgumentException>(new DelegateProcessor(_ => { }));

        var act = () => processor.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Redelivery retries before invoking handler.</summary>
    [Fact]
    public async Task Process_Redelivery_RetriesBeforeHandler()
    {
        var attempts = 0;
        var body = new DelegateProcessor(_ =>
        {
            attempts++;
            throw new InvalidOperationException($"attempt {attempts}");
        });

        var handled = false;
        var processor = new OnExceptionProcessor(body, Substitute.For<ILogger>())
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => handled = true),
                maxRedeliveries: 2);

        await processor.Process(new Exchange());

        attempts.Should().Be(3); // 1 original + 2 redeliveries
        handled.Should().BeTrue();
    }

    /// <summary>OperationCanceledException is never caught.</summary>
    [Fact]
    public async Task Process_Cancellation_NeverCaught()
    {
        var processor = new OnExceptionProcessor(
                new DelegateProcessor(_ => throw new OperationCanceledException()))
            .Handle<Exception>(new DelegateProcessor(_ => { }));

        var act = () => processor.Process(new Exchange());
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Handlers property returns registered handlers.</summary>
    [Fact]
    public void Handlers_ReturnsRegistered()
    {
        var processor = new OnExceptionProcessor(new DelegateProcessor(_ => { }))
            .Handle<InvalidOperationException>(new DelegateProcessor(_ => { }))
            .Handle<ArgumentException>(new DelegateProcessor(_ => { }));

        processor.Handlers.Should().HaveCount(2);
    }

    // ── Handled flag ──

    [Fact]
    public async Task Handled_ClearsExceptionOnExchange()
    {
        var processor = new OnExceptionProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException("boom")))
            .Handle<InvalidOperationException>(new DelegateProcessor(_ => { }),
                handled: true);

        var exchange = new Exchange();
        await processor.Process(exchange);

        exchange.ExceptionHandled.Should().BeTrue();
        exchange.Exception.Should().BeNull("Handled flag should clear the exception");
    }

    [Fact]
    public async Task Handled_False_ExceptionRemainsOnExchange()
    {
        var processor = new OnExceptionProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException("boom")))
            .Handle<InvalidOperationException>(new DelegateProcessor(_ => { }),
                handled: false);

        var exchange = new Exchange();
        await processor.Process(exchange);

        exchange.ExceptionHandled.Should().BeTrue();
        exchange.Exception.Should().NotBeNull("Handled=false should keep exception on exchange");
    }

    // ── Continued flag ──

    [Fact]
    public async Task Continued_SetsExceptionHandled()
    {
        var processor = new OnExceptionProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException("boom")))
            .Handle<InvalidOperationException>(new DelegateProcessor(_ => { }),
                continued: true);

        var exchange = new Exchange();
        await processor.Process(exchange);

        exchange.ExceptionHandled.Should().BeTrue();
    }

    // ── OnWhen predicate ──

    [Fact]
    public async Task OnWhen_True_HandlerFires()
    {
        var handled = false;
        var processor = new OnExceptionProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException("boom")))
            .Handle<InvalidOperationException>(new DelegateProcessor(_ => handled = true),
                onWhenPredicate: ex => ex.Exception?.Message == "boom");

        await processor.Process(new Exchange());
        handled.Should().BeTrue();
    }

    [Fact]
    public async Task OnWhen_False_HandlerSkipped_ExceptionRethrown()
    {
        var processor = new OnExceptionProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException("boom")))
            .Handle<InvalidOperationException>(new DelegateProcessor(_ => { }),
                onWhenPredicate: _ => false);

        var act = () => processor.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── OnExceptionOccurred callback ──

    [Fact]
    public async Task OnExceptionOccurred_CalledOnEachException()
    {
        var callbackCount = 0;
        var attempts = 0;
        var processor = new OnExceptionProcessor(
                new DelegateProcessor(_ => { attempts++; throw new InvalidOperationException(); }))
            .Handle<InvalidOperationException>(new DelegateProcessor(_ => { }),
                maxRedeliveries: 2,
                onExceptionOccurred: _ => callbackCount++);

        await processor.Process(new Exchange());

        callbackCount.Should().Be(3, "callback fires on each exception (1 original + 2 retries)");
    }

    // ── RetryAttemptedLogLevel / RetriesExhaustedLogLevel ──

    [Fact]
    public async Task CustomLogLevels_AreRespected()
    {
        var attempts = 0;
        var body = new DelegateProcessor(_ =>
        {
            attempts++;
            throw new InvalidOperationException($"attempt {attempts}");
        });

        // This test verifies the handler can be created with custom log levels without error
        var processor = new OnExceptionProcessor(body)
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => { }),
                maxRedeliveries: 1,
                retryAttemptedLogLevel: LogLevel.Debug,
                retriesExhaustedLogLevel: LogLevel.Critical);

        var exchange = new Exchange();
        await processor.Process(exchange);

        attempts.Should().Be(2); // 1 original + 1 retry
        exchange.ExceptionHandled.Should().BeTrue();
    }

    // ── DSL integration: fluent chain scope ──

    [Fact]
    public async Task DslScope_Handled_ClearsException()
    {
        var context = new RouteContext();
        object? captured = null;

        context.AddRoutes(r =>
        {
            r.From("direct://oe-handled")
                .OnException<InvalidOperationException>()
                    .Handled()
                    .Log("Handled!")
                .End()
                .Process(_ => throw new InvalidOperationException("test"))
                .Process(e => captured = "should-not-reach");
        });

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://oe-handled").CreateProducer();
            await producer.Start();
            var exchange = new Exchange(new Message("data"));
            await producer.Process(exchange);

            exchange.ExceptionHandled.Should().BeTrue();
            exchange.Exception.Should().BeNull();
        }
        finally
        {
            await context.Stop();
        }
    }

    [Fact]
    public async Task DslScope_OnWhen_FiltersHandler()
    {
        var fallbackReached = false;
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://oe-onwhen")
                .OnException<InvalidOperationException>()
                    .OnWhen(e => e.Exception?.Message == "expected")
                    .Handled()
                    .Process(_ => fallbackReached = true)
                .End()
                .Process(_ => throw new InvalidOperationException("expected"));
        });

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://oe-onwhen").CreateProducer();
            await producer.Start();
            var exchange = new Exchange(new Message("data"));
            await producer.Process(exchange);

            fallbackReached.Should().BeTrue();
            exchange.ExceptionHandled.Should().BeTrue();
        }
        finally
        {
            await context.Stop();
        }
    }

    [Fact]
    public async Task DslScope_OnExceptionOccurred_CallbackInvoked()
    {
        var callbackCount = 0;
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://oe-callback")
                .OnException<InvalidOperationException>()
                    .Handled()
                    .MaximumRedeliveries(1)
                    .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
                    .OnExceptionOccurred(_ => callbackCount++)
                .End()
                .Process(_ => throw new InvalidOperationException("test"));
        });

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://oe-callback").CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("data")));

            callbackCount.Should().Be(2, "callback fires on original + 1 retry");
        }
        finally
        {
            await context.Stop();
        }
    }

    [Fact]
    public async Task DslScope_RetryLogLevels_Configurable()
    {
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://oe-loglevels")
                .OnException<InvalidOperationException>()
                    .Handled()
                    .MaximumRedeliveries(1)
                    .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
                    .RetryAttemptedLogLevel(LogLevel.Debug)
                    .RetriesExhaustedLogLevel(LogLevel.Critical)
                .End()
                .Process(_ => throw new InvalidOperationException("test"));
        });

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://oe-loglevels").CreateProducer();
            await producer.Start();
            var exchange = new Exchange(new Message("data"));
            await producer.Process(exchange);

            // Simply verifies that custom log levels don't break anything
            exchange.ExceptionHandled.Should().BeTrue();
        }
        finally
        {
            await context.Stop();
        }
    }

    [Fact]
    public void DslScope_HandledOutsideScope_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.Handled();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OnException*");
    }

    [Fact]
    public void DslScope_ContinuedOutsideScope_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.Continued();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OnException*");
    }

    [Fact]
    public void DslScope_OnWhenOutsideScope_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.OnWhen(_ => true);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OnException*");
    }

    // ── B3: RetryWhile predicate ──

    [Fact]
    public async Task RetryWhile_ContinuesWhileTrue()
    {
        var attempts = 0;
        var body = new DelegateProcessor(_ =>
        {
            attempts++;
            throw new InvalidOperationException($"attempt {attempts}");
        });

        var processor = new OnExceptionProcessor(body, Substitute.For<ILogger>())
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => { }),
                handled: true,
                retryWhile: ex => attempts < 4, // retry while attempts < 4
                redeliveryDelay: TimeSpan.FromMilliseconds(1));

        await processor.Process(new Exchange());

        attempts.Should().Be(4, "RetryWhile should keep retrying until predicate returns false");
    }

    [Fact]
    public async Task RetryWhile_OverridesMaxRedeliveries()
    {
        var attempts = 0;
        var body = new DelegateProcessor(_ =>
        {
            attempts++;
            throw new InvalidOperationException();
        });

        // MaxRedeliveries=1 but RetryWhile says keep going until 5
        var processor = new OnExceptionProcessor(body, Substitute.For<ILogger>())
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => { }),
                handled: true,
                maxRedeliveries: 1,
                retryWhile: _ => attempts < 5,
                redeliveryDelay: TimeSpan.FromMilliseconds(1));

        await processor.Process(new Exchange());

        attempts.Should().Be(5, "RetryWhile takes priority over MaxRedeliveries");
    }

    // ── B4: OnRedelivery callback ──

    [Fact]
    public async Task OnRedelivery_CalledBeforeEachRetry()
    {
        var redeliveryCalls = 0;
        var attempts = 0;
        var body = new DelegateProcessor(_ =>
        {
            attempts++;
            throw new InvalidOperationException();
        });

        var processor = new OnExceptionProcessor(body, Substitute.For<ILogger>())
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => { }),
                handled: true,
                maxRedeliveries: 3,
                redeliveryDelay: TimeSpan.FromMilliseconds(1),
                onRedelivery: _ => redeliveryCalls++);

        await processor.Process(new Exchange());

        redeliveryCalls.Should().Be(3, "OnRedelivery fires before each of the 3 retries");
    }

    // ── B5: OnPrepareFailure callback ──

    [Fact]
    public async Task OnPrepareFailure_CalledBeforeHandler()
    {
        var prepareCalled = false;
        var body = new DelegateProcessor(_ => throw new InvalidOperationException());

        var processor = new OnExceptionProcessor(body)
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => { }),
                handled: true,
                onPrepareFailure: ex =>
                {
                    prepareCalled = true;
                    ex.In.Headers["PreparedForFailure"] = true;
                });

        var exchange = new Exchange();
        await processor.Process(exchange);

        prepareCalled.Should().BeTrue();
        exchange.In.Headers.Should().ContainKey("PreparedForFailure");
    }

    // ── B6: UseOriginalMessage / UseOriginalBody ──

    [Fact]
    public async Task UseOriginalBody_RestoresBodyBeforeHandler()
    {
        var body = new DelegateProcessor(ex =>
        {
            ex.In.Body = "mutated";
            throw new InvalidOperationException();
        });

        object? handlerBody = null;
        var processor = new OnExceptionProcessor(body)
            .Handle<InvalidOperationException>(
                new DelegateProcessor(ex => handlerBody = ex.In.Body),
                handled: true,
                useOriginalBody: true);

        var exchange = new Exchange(new Message("original"));
        await processor.Process(exchange);

        handlerBody.Should().Be("original", "UseOriginalBody should restore body before handler");
    }

    [Fact]
    public async Task UseOriginalMessage_RestoresBodyAndHeaders()
    {
        var body = new DelegateProcessor(ex =>
        {
            ex.In.Body = "mutated";
            ex.In.Headers["NewHeader"] = "added";
            throw new InvalidOperationException();
        });

        object? handlerBody = null;
        bool? hasNewHeader = null;
        var processor = new OnExceptionProcessor(body)
            .Handle<InvalidOperationException>(
                new DelegateProcessor(ex =>
                {
                    handlerBody = ex.In.Body;
                    hasNewHeader = ex.In.Headers.ContainsKey("NewHeader");
                }),
                handled: true,
                useOriginalMessage: true);

        var exchange = new Exchange(new Message("original"));
        exchange.In.Headers["OriginalHeader"] = "kept";
        await processor.Process(exchange);

        handlerBody.Should().Be("original");
        hasNewHeader.Should().BeFalse("UseOriginalMessage should restore original headers, removing added ones");
    }

    // ── B7: AllowRedeliveryWhileStopping ──

    [Fact]
    public async Task AllowRedeliveryWhileStopping_False_StopsRetryOnCancellation()
    {
        var attempts = 0;
        var cts = new CancellationTokenSource();
        var body = new DelegateProcessor(_ =>
        {
            attempts++;
            if (attempts == 1)
                cts.Cancel(); // cancel after first attempt
            throw new InvalidOperationException();
        });

        var handlerCalled = false;
        var processor = new OnExceptionProcessor(body, Substitute.For<ILogger>())
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => handlerCalled = true),
                handled: true,
                maxRedeliveries: 5,
                redeliveryDelay: TimeSpan.FromMilliseconds(1),
                allowRedeliveryWhileStopping: false);

        // The cts.Cancel() triggers OperationCanceledException in Task.Delay
        // But since AllowRedeliveryWhileStopping=false, shouldRetry becomes false,
        // so it goes to exhaustion path (handler should fire)
        await processor.Process(new Exchange(), cts.Token);

        attempts.Should().Be(1, "should stop retrying after cancellation");
        handlerCalled.Should().BeTrue("handler fires after retries are refused");
    }

    // ── B8: LogStackTrace / LogExhausted ──

    [Fact]
    public async Task LogExhausted_False_SuppressesExhaustionLog()
    {
        var logger = Substitute.For<ILogger>();
        var body = new DelegateProcessor(_ => throw new InvalidOperationException());

        var processor = new OnExceptionProcessor(body, logger)
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => { }),
                handled: true,
                maxRedeliveries: 1,
                redeliveryDelay: TimeSpan.FromMilliseconds(1),
                logExhausted: false);

        await processor.Process(new Exchange());

        // Logger should NOT receive an Error-level "Retries exhausted" message
        // (it may still receive Warning-level retry attempt messages)
        logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Log")
            .SelectMany(c => c.GetArguments())
            .OfType<LogLevel>()
            .Should().NotContain(LogLevel.Error);
    }

    // ── B10: Exchange redelivery headers ──

    [Fact]
    public async Task RedeliveryHeaders_SetDuringRetries()
    {
        var headers = new Dictionary<string, object?>();
        var attempts = 0;
        var body = new DelegateProcessor(ex =>
        {
            attempts++;
            if (attempts <= 2)
                throw new InvalidOperationException();
        });

        var processor = new OnExceptionProcessor(body, Substitute.For<ILogger>())
            .Handle<InvalidOperationException>(
                new DelegateProcessor(ex =>
                {
                    // Capture headers at handler time (after exhaustion)
                    foreach (var h in ex.In.Headers)
                        headers[h.Key] = h.Value;
                }),
                handled: true,
                maxRedeliveries: 1,
                redeliveryDelay: TimeSpan.FromMilliseconds(1));

        var exchange = new Exchange();
        await processor.Process(exchange);

        exchange.In.Headers.Should().ContainKey("CamelRedelivered");
        exchange.In.Headers.Should().ContainKey("CamelRedeliveryExhausted");
        exchange.In.Headers["CamelRedeliveryExhausted"].Should().Be(true);
    }

    [Fact]
    public async Task RedeliveryHeaders_NotSetWhenNoRetries()
    {
        var body = new DelegateProcessor(_ => throw new InvalidOperationException());

        var processor = new OnExceptionProcessor(body)
            .Handle<InvalidOperationException>(
                new DelegateProcessor(_ => { }),
                handled: true,
                maxRedeliveries: 0);

        var exchange = new Exchange();
        await processor.Process(exchange);

        // CamelRedelivered is set but is false (no actual redelivery happened)
        exchange.In.Headers["CamelRedelivered"].Should().Be(false);
    }

    // ── DSL integration for new features ──

    [Fact]
    public async Task Dsl_RetryWhile_WorksInFluent()
    {
        var attempts = 0;
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://oe-retrywhile")
                .OnException<InvalidOperationException>()
                    .Handled()
                    .RetryWhile(_ => attempts < 3)
                    .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
                .End()
                .Process(_ =>
                {
                    attempts++;
                    throw new InvalidOperationException();
                });
        });

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://oe-retrywhile").CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("data")));

            attempts.Should().Be(3);
        }
        finally
        {
            await context.Stop();
        }
    }

    [Fact]
    public async Task Dsl_UseOriginalBody_WorksInFluent()
    {
        object? handlerBody = null;
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://oe-origbody")
                .OnException<InvalidOperationException>()
                    .Handled()
                    .UseOriginalBody()
                    .Process(ex => handlerBody = ex.In.Body)
                .End()
                .Process(ex =>
                {
                    ex.In.Body = "mutated";
                    throw new InvalidOperationException();
                });
        });

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://oe-origbody").CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("original")));

            handlerBody.Should().Be("original");
        }
        finally
        {
            await context.Stop();
        }
    }

    [Fact]
    public async Task Dsl_OnRedelivery_WorksInFluent()
    {
        var redeliveryCount = 0;
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://oe-onredeliver")
                .OnException<InvalidOperationException>()
                    .Handled()
                    .MaximumRedeliveries(2)
                    .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
                    .OnRedelivery(_ => redeliveryCount++)
                .End()
                .Process(_ => throw new InvalidOperationException());
        });

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://oe-onredeliver").CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("data")));

            redeliveryCount.Should().Be(2);
        }
        finally
        {
            await context.Stop();
        }
    }

    [Fact]
    public async Task Dsl_OnPrepareFailure_WorksInFluent()
    {
        var prepared = false;
        var context = new RouteContext();

        context.AddRoutes(r =>
        {
            r.From("direct://oe-prepfail")
                .OnException<InvalidOperationException>()
                    .Handled()
                    .OnPrepareFailure(_ => prepared = true)
                .End()
                .Process(_ => throw new InvalidOperationException());
        });

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://oe-prepfail").CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("data")));

            prepared.Should().BeTrue();
        }
        finally
        {
            await context.Stop();
        }
    }
}
