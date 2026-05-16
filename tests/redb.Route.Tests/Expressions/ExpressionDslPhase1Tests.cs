using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for Expression DSL Phase 1: LoopExpression, DelayExpression, ThrottleExpression,
/// SetPropertyExpression, SetBodyExpression, SetHeaderExpression, TransformExpression.
/// </summary>
public class ExpressionDslPhase1Tests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ─── SetPropertyExpression ───

    [Fact]
    public async Task SetPropertyExpression_ResolvesFromHeader()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://set-prop-expr")
                .SetPropertyExpression("greeting", "${header.name}")
                .Process(e => captured = e.Properties["greeting"]);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "test" });
        exchange.In.Headers["name"] = "Alice";

        var producer = _context.GetEndpoint("direct://set-prop-expr").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("Alice");
    }

    [Fact]
    public async Task SetPropertyExpression_ResolvesTemplate()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://set-prop-tmpl")
                .SetPropertyExpression("msg", "Hello ${header.name}!")
                .Process(e => captured = e.Properties["msg"]);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "test" });
        exchange.In.Headers["name"] = "Bob";

        var producer = _context.GetEndpoint("direct://set-prop-tmpl").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("Hello Bob!");
    }

    // ─── LoopExpression ───

    [Fact]
    public async Task LoopExpression_CountFromHeader()
    {
        int counter = 0;
        _context.AddRoutes(r =>
        {
            r.From("direct://loop-expr")
                .LoopExpression("${header.count}", sub =>
                {
                    sub.Process(_ => Interlocked.Increment(ref counter));
                });
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "test" });
        exchange.In.Headers["count"] = 5;

        var producer = _context.GetEndpoint("direct://loop-expr").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        counter.Should().Be(5);
    }

    [Fact]
    public async Task LoopExpression_CountFromStringHeader()
    {
        int counter = 0;
        _context.AddRoutes(r =>
        {
            r.From("direct://loop-expr-str")
                .LoopExpression("${header.count}", sub =>
                {
                    sub.Process(_ => Interlocked.Increment(ref counter));
                });
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "test" });
        exchange.In.Headers["count"] = "3";

        var producer = _context.GetEndpoint("direct://loop-expr-str").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        counter.Should().Be(3);
    }

    [Fact]
    public async Task LoopExpression_WithCopy_BodyPreserved()
    {
        string? lastBody = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://loop-expr-copy")
                .LoopExpression("${header.count}", sub =>
                {
                    sub.Process(e =>
                    {
                        lastBody = e.In.Body?.ToString();
                        e.In.Body = "modified";
                    });
                }, copy: true);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "original" });
        exchange.In.Headers["count"] = 3;

        var producer = _context.GetEndpoint("direct://loop-expr-copy").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        // With copy, each iteration starts from the original
        lastBody.Should().Be("original");
    }

    // ─── DelayExpression ───

    [Fact]
    public async Task DelayExpression_DelaysFromHeader()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://delay-expr")
                .DelayExpression("${header.delayMs}")
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "delayed" });
        exchange.In.Headers["delayMs"] = 50;

        var producer = _context.GetEndpoint("direct://delay-expr").CreateProducer();
        await producer.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await producer.Process(exchange);
        sw.Stop();

        captured.Should().Be("delayed");
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(30); // ~50ms with some tolerance
    }

    // ─── ThrottleExpression ───

    [Fact]
    public async Task ThrottleExpression_ResolvesFromHeader()
    {
        int count = 0;
        _context.AddRoutes(r =>
        {
            r.From("direct://throttle-expr")
                .ThrottleExpression("${header.rate}", TimeSpan.FromSeconds(1))
                .Process(_ => Interlocked.Increment(ref count));
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://throttle-expr").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "test" });
        exchange.In.Headers["rate"] = 100;
        await producer.Process(exchange);

        count.Should().Be(1);
    }

    // ─── SetBodyExpression ───

    [Fact]
    public async Task SetBodyExpression_ResolvesTemplate()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://setbody-expr")
                .SetBodyExpression("${header.greeting} ${header.name}")
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "original" });
        exchange.In.Headers["greeting"] = "Hello";
        exchange.In.Headers["name"] = "World";

        var producer = _context.GetEndpoint("direct://setbody-expr").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("Hello World");
    }

    // ─── TransformExpression ───

    [Fact]
    public async Task TransformExpression_ResolvesTemplate()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://transform-expr")
                .TransformExpression("Processed: ${body}")
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "data" });

        var producer = _context.GetEndpoint("direct://transform-expr").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("Processed: data");
    }

    // ─── SetHeaderExpression ───

    [Fact]
    public async Task SetHeaderExpression_ResolvesFromBody()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://sethdr-expr")
                .SetHeaderExpression("bodyRef", "${body}")
                .Process(e => captured = e.In.Headers["bodyRef"]);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "my-data" });

        var producer = _context.GetEndpoint("direct://sethdr-expr").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("my-data");
    }
}
