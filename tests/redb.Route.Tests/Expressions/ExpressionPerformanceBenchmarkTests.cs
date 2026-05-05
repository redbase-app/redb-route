using System.Diagnostics;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Performance benchmarks for the expression compilation and evaluation engine.
/// Measures cache hit throughput, AST compilation, and template processing performance.
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionPerformanceBenchmarkTests : IDisposable
{
    private const int WarmupIterations = 100;
    private const int BenchmarkIterations = 10_000;

    public ExpressionPerformanceBenchmarkTests()
    {
        ExpressionResolver.ClearAllCaches();
    }

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
    }

    private static IExchange CreateExchange()
    {
        var exchange = new Exchange(new Message("test body"));
        exchange.In.Headers["name"] = "Alice";
        exchange.In.Headers["count"] = "42";
        exchange.Properties["x"] = 10;
        exchange.Properties["y"] = 20;
        exchange.Properties["flag"] = true;
        exchange.Properties["items"] = new List<string> { "alpha", "beta", "gamma" };
        exchange.Properties["label"] = "test";
        return exchange;
    }

    [Fact]
    public void Benchmark_SimpleTemplateProcessing()
    {
        var exchange = CreateExchange();

        // Warmup
        for (int i = 0; i < WarmupIterations; i++)
            ExpressionResolver.ProcessTemplate("Hello ${header.name}!", exchange);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
            ExpressionResolver.ProcessTemplate("Hello ${header.name}!", exchange);
        sw.Stop();

        var opsPerSecond = BenchmarkIterations / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(20_000, 
            $"Simple template processing should be fast (was {opsPerSecond:N0} ops/s in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void Benchmark_ArithmeticExpression()
    {
        var exchange = CreateExchange();

        for (int i = 0; i < WarmupIterations; i++)
            ExpressionResolver.ResolveExpression("x + y", exchange);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
            ExpressionResolver.ResolveExpression("x + y", exchange);
        sw.Stop();

        var opsPerSecond = BenchmarkIterations / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(10_000,
            $"Arithmetic expression should be fast (was {opsPerSecond:N0} ops/s in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void Benchmark_NullCoalescing()
    {
        var exchange = CreateExchange();

        for (int i = 0; i < WarmupIterations; i++)
            ExpressionResolver.ResolveExpression("label ?? 'default'", exchange);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
            ExpressionResolver.ResolveExpression("label ?? 'default'", exchange);
        sw.Stop();

        var opsPerSecond = BenchmarkIterations / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(10_000,
            $"Null-coalescing should be fast (was {opsPerSecond:N0} ops/s in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void Benchmark_TernaryExpression()
    {
        var exchange = CreateExchange();

        for (int i = 0; i < WarmupIterations; i++)
            ExpressionResolver.ResolveExpression("flag ? 'yes' : 'no'", exchange);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
            ExpressionResolver.ResolveExpression("flag ? 'yes' : 'no'", exchange);
        sw.Stop();

        var opsPerSecond = BenchmarkIterations / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(10_000,
            $"Ternary expression should be fast (was {opsPerSecond:N0} ops/s in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void Benchmark_ConcatFunction()
    {
        var exchange = CreateExchange();

        for (int i = 0; i < WarmupIterations; i++)
            ExpressionResolver.ResolveExpression("concat(label, ' ', label)", exchange);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
            ExpressionResolver.ResolveExpression("concat(label, ' ', label)", exchange);
        sw.Stop();

        var opsPerSecond = BenchmarkIterations / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(10_000,
            $"concat() should be fast (was {opsPerSecond:N0} ops/s in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void Benchmark_IndexAccess()
    {
        var exchange = CreateExchange();

        for (int i = 0; i < WarmupIterations; i++)
            ExpressionResolver.ResolveExpression("items[0]", exchange);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
            ExpressionResolver.ResolveExpression("items[0]", exchange);
        sw.Stop();

        var opsPerSecond = BenchmarkIterations / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(10_000,
            $"Index access should be fast (was {opsPerSecond:N0} ops/s in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void Benchmark_ComplexTemplate()
    {
        var exchange = CreateExchange();
        var template = "Name: ${header.name}, Sum: ${x + y}, First: ${items[0]}";

        for (int i = 0; i < WarmupIterations; i++)
            ExpressionResolver.ProcessTemplate(template, exchange);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
            ExpressionResolver.ProcessTemplate(template, exchange);
        sw.Stop();

        var opsPerSecond = BenchmarkIterations / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(2_000,
            $"Complex template should be reasonable (was {opsPerSecond:N0} ops/s in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void Benchmark_CacheHitRate()
    {
        var exchange = CreateExchange();
        var expressions = new[] { "x + y", "label ?? 'def'", "flag ? 'a' : 'b'", "items[0]", "concat('a', 'b')" };

        // Warmup — populate cache
        foreach (var expr in expressions)
            ExpressionResolver.ResolveExpression(expr, exchange);

        // Measure cached access
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
        {
            var expr = expressions[i % expressions.Length];
            ExpressionResolver.ResolveExpression(expr, exchange);
        }
        sw.Stop();

        var opsPerSecond = BenchmarkIterations / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(10_000,
            $"Mixed cached access should be fast (was {opsPerSecond:N0} ops/s in {sw.ElapsedMilliseconds}ms)");
    }
}
