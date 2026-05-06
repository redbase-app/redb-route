using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for builder-level <c>OnException&lt;T&gt;()</c> — global exception handlers
/// defined at the RouteBuilder level and compiled into the RouteContext.
/// </summary>
public class BuilderOnExceptionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ── Builder DSL ──

    [Fact]
    public void OnException_RegistersExceptionDefinition()
    {
        var builder = new TestBuilder(configure: b =>
        {
            b.OnException<InvalidOperationException>()
                .MaximumRedeliveries(3);
        });

        ((IRouteBuilder)builder).Configure(null!);
        builder.ExceptionDefCount.Should().Be(1);
    }

    [Fact]
    public void OnException_MultipleTypes_RegistersSeparateDefinitions()
    {
        var builder = new TestBuilder(configure: b =>
        {
            b.OnException<InvalidOperationException>()
                .Handled();

            b.OnException<TimeoutException>()
                .MaximumRedeliveries(5);
        });

        ((IRouteBuilder)builder).Configure(null!);
        builder.ExceptionDefCount.Should().Be(2);
    }

    [Fact]
    public void OnException_SupportsDslChaining()
    {
        // Verify all DSL methods can be chained on builder-level OnException
        var builder = new TestBuilder(configure: b =>
        {
            b.OnException<Exception>()
                .MaximumRedeliveries(3)
                .RedeliveryDelay(TimeSpan.FromSeconds(1))
                .BackOffMultiplier(2.0)
                .UseExponentialBackOff()
                .Handled()
                .OnWhen(e => e.In.Body is string)
                .RetryAttemptedLogLevel(Microsoft.Extensions.Logging.LogLevel.Debug)
                .RetriesExhaustedLogLevel(Microsoft.Extensions.Logging.LogLevel.Critical)
                .OnExceptionOccurred(_ => { })
                .Log("Error occurred")
                .To("direct://dead-letter");
        });

        ((IRouteBuilder)builder).Configure(null!);
        builder.ExceptionDefCount.Should().Be(1);
    }

    // ── Engine Integration ──

    [Fact]
    public async Task OnException_CompilesIntoGlobalHandler()
    {
        Exception? capturedException = null;

        _context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>()
                .Handled()
                .Process(e => capturedException = e.Exception);

            r.From("direct://input")
                .Process(_ => throw new InvalidOperationException("boom"));
        });

        await _context.Start();

        // Send message — exception should be caught by global handler
        var endpoint = _context.GetEndpoint("direct://input");
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("test"));
        await producer.Process(exchange);

        capturedException.Should().NotBeNull();
        capturedException.Should().BeOfType<InvalidOperationException>();
        capturedException!.Message.Should().Be("boom");
    }

    [Fact]
    public async Task OnException_WithRetry_RetriesBeforeHandling()
    {
        var callCount = 0;

        _context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>()
                .MaximumRedeliveries(2)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(10))
                .Handled();

            r.From("direct://input")
                .Process(_ =>
                {
                    callCount++;
                    throw new InvalidOperationException("retry me");
                });
        });

        await _context.Start();

        var endpoint = _context.GetEndpoint("direct://input");
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("go"));
        await producer.Process(exchange);

        // Original call + 2 retries = 3 total
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task OnException_DerivedType_CaughtByBaseHandler()
    {
        var handled = false;

        _context.AddRoutes(r =>
        {
            // Register handler for base Exception type
            r.OnException<Exception>()
                .Handled()
                .Process(_ => handled = true);

            r.From("direct://input")
                .Process(_ => throw new ArgumentException("derived"));
        });

        await _context.Start();

        var endpoint = _context.GetEndpoint("direct://input");
        var producer = endpoint.CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("test")));

        handled.Should().BeTrue();
    }

    [Fact]
    public async Task OnException_HandledClears_ExceptionOnExchange()
    {
        _context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>()
                .Handled()
                .Process(_ => { });

            r.From("direct://input")
                .Process(_ => throw new InvalidOperationException("cleared"));
        });

        await _context.Start();

        var endpoint = _context.GetEndpoint("direct://input");
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("test"));
        await producer.Process(exchange);

        // With Handled, exception should be cleared on the exchange
        exchange.Exception.Should().BeNull();
    }

    [Fact]
    public void HasExceptionRoute_ReturnsTrue_AfterBuilderOnException()
    {
        _context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>()
                .Handled();

            r.From("direct://input")
                .To("direct://output");
        });

        // Start configures and compiles
        _context.Start().GetAwaiter().GetResult();

        _context.HasExceptionRoute<InvalidOperationException>().Should().BeTrue();
        _context.HasExceptionRoute<TimeoutException>().Should().BeFalse();
    }

    // ── Test Helpers ──

    /// <summary>Builder that exposes OnException for testing.</summary>
    private sealed class TestBuilder : RouteBuilder
    {
        private readonly Action<TestBuilder>? _configure;
        private int _exceptionDefCount;

        internal TestBuilder(Action<TestBuilder>? configure = null) => _configure = configure;

        protected override void Configure() => _configure?.Invoke(this);

        public new IRouteDefinition OnException<T>() where T : Exception
        {
            _exceptionDefCount++;
            return base.OnException<T>();
        }

        public IRouteDefinition CallFrom(string uri) => From(uri);

        public int ExceptionDefCount => _exceptionDefCount;
    }
}
