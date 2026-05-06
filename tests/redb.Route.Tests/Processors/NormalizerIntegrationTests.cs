using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for Normalizer using full route compilation and pipeline execution.
/// </summary>
[Trait("Category", "Integration")]
public class NormalizerIntegrationTests
{
    private readonly RouteContext _context = new();

    private static IExchange CreateExchange(object? body = null, IDictionary<string, object?>? headers = null)
    {
        var msg = new Message(body);
        if (headers is not null)
        {
            foreach (var kv in headers)
                msg.Headers[kv.Key] = kv.Value;
        }
        return Exchange.Create(msg, null);
    }

    // ══════════════════════════════════════════════════════════════
    // Basic predicate routing
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task When_MatchingPredicate_AppliesTransform()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .When(e => e.In.Body is string, e => ((string)e.In.Body!).ToUpperInvariant())
            .When(e => e.In.Body is int, e => ((int)e.In.Body!) * 10));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var exchange = CreateExchange("hello");
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("HELLO");
    }

    [Fact]
    public async Task When_SecondPredicate_MatchesCorrectBranch()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .When(e => e.In.Body is string, e => "was-string")
            .When(e => e.In.Body is int, e => "was-int"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var exchange = CreateExchange(42);
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("was-int");
    }

    [Fact]
    public async Task When_NoMatch_BodyUnchanged()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .When(e => e.In.Body is string, e => "was-string"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var exchange = CreateExchange(42);
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be(42);
    }

    // ══════════════════════════════════════════════════════════════
    // Otherwise fallback
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Otherwise_AppliedWhenNoPredicateMatches()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .When(e => e.In.Body is string, e => "was-string")
            .Otherwise(e => "unknown-format"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var exchange = CreateExchange(new object());
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("unknown-format");
    }

    [Fact]
    public async Task Otherwise_NotApplied_WhenPredicateMatches()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .When(e => e.In.Body is string, e => "was-string")
            .Otherwise(e => "fallback"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var exchange = CreateExchange("test");
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("was-string");
    }

    // ══════════════════════════════════════════════════════════════
    // ContentType-based routing
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenContentType_MatchesJsonHeader()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .WhenContentType("application/json", e => $"json:{e.In.Body}")
            .WhenContentType("application/xml", e => $"xml:{e.In.Body}"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var exchange = CreateExchange("data",
            new Dictionary<string, object?> { ["ContentType"] = "application/json" });
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("json:data");
    }

    [Fact]
    public async Task WhenContentType_CaseInsensitive()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .WhenContentType("application/xml", e => "xml-normalized"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var exchange = CreateExchange("data",
            new Dictionary<string, object?> { ["ContentType"] = "APPLICATION/XML" });
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("xml-normalized");
    }

    // ══════════════════════════════════════════════════════════════
    // Mixed clauses
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task MixedClauses_PredicateAndContentType()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .When(e => e.In.Body is int, e => string.Format(CultureInfo.InvariantCulture, "int:{0}", e.In.Body))
            .WhenContentType("text/csv", e => string.Format(CultureInfo.InvariantCulture, "csv:{0}", e.In.Body))
            .Otherwise(e => string.Format(CultureInfo.InvariantCulture, "other:{0}", e.In.Body)));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        // int body → predicate branch
        var e1 = CreateExchange(100);
        await pipeline.Process(e1);
        e1.In.Body.Should().Be("int:100");

        // text/csv → content-type branch
        var e2 = CreateExchange("a,b,c",
            new Dictionary<string, object?> { ["ContentType"] = "text/csv" });
        await pipeline.Process(e2);
        e2.In.Body.Should().Be("csv:a,b,c");

        // no match → otherwise
        var e3 = CreateExchange(3.14);
        await pipeline.Process(e3);
        e3.In.Body.Should().Be("other:3.14");
    }

    // ══════════════════════════════════════════════════════════════
    // Pipeline chaining
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Normalize_ChainsWithSubsequentSteps()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .When(e => e.In.Body is string, e => ((string)e.In.Body!).ToUpperInvariant()));
        def.SetHeader("Normalized", "true");

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var exchange = CreateExchange("hello");
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("HELLO");
        exchange.In.Headers["Normalized"].Should().Be("true");
    }

    [Fact]
    public async Task Normalize_PreservesProperties()
    {
        var def = new RouteDefinition();
        def.From("direct://normalize");
        def.Normalize(n => n
            .When(e => true, e => "normalized"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var exchange = CreateExchange("raw");
        exchange.Properties["traceId"] = "abc-123";
        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("normalized");
        exchange.Properties["traceId"].Should().Be("abc-123");
    }
}
