using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Dsl;

/// <summary>
/// Integration tests for deeply nested fluent scopes after the RouteStep removal.
/// Validates two things at once:
///  1) The DSL compiles end-to-end (Choice → When → RichLog → Split → TryCatch → ...)
///     using the full Camel-style scope toolbox with mixed End*() / End() closers.
///  2) The runtime tree produces the expected processor behavior — split fan-out,
///     log capture (rich logs with route id + headers + properties), Catch handling,
///     Otherwise fallback.
/// </summary>
public class DeepNestedDslTests
{
    // ── Test logger that records every entry ─────────────────────────────────

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }

        internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Entries.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }

    private static (RouteContext Ctx, CapturingLoggerProvider Capture) NewContext()
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(capture); });
        var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var ctx = new RouteContext(provider, loggerFactory: loggerFactory);
        ctx.AddComponent(new Route.Components.DirectComponent());
        return (ctx, capture);
    }

    private static IExchange NewExchange(object? body, IDictionary<string, object?>? headers = null)
    {
        var ex = Exchange.Create(new Message(body), null);
        if (headers is not null)
            foreach (var kv in headers) ex.In.Headers[kv.Key] = kv.Value;
        return ex;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 1 — Choice → When → RichLog → Split → Process×N + dyn-Log → EndLog,
    //               sibling-When (string) and Otherwise reachable via the
    //               extension methods on IRouteDefinition (typed parent recovery
    //               after EndSplit).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeepNest_ChoiceWhenSplitRichLog_ExecutesAndLogs()
    {
        var (ctx, capture) = NewContext();

        var processed = new ConcurrentBag<string>();

        var route = new RouteDefinition().RouteId("deep-1");
        route.From("direct:in")
             .SetHeader("step", "start")
             .Choice()
                 .When(e => e.In.Body is IEnumerable<string> && e.In.Body is not string)
                     .SetHeader("branch", "list")
                     .Log(LogLevel.Information)
                         .Message("opening list branch")
                         .Header("branch")
                         .Property("trace")
                         .ShowRouteId(true)
                     .EndLog()
                     .Split((IExchange e) => (IEnumerable<object?>)((IEnumerable<string>)e.In.Body!).Cast<object?>())
                         .Process(e =>
                         {
                             var s = (string)e.In.Body!;
                             processed.Add(s.ToUpperInvariant());
                         })
                         .Log(LogLevel.Debug)
                             .Message(e => $"processed item={e.In.Body}")
                             .Message("after-item")
                         .EndLog()
                     .EndSplit()
                     .Log(LogLevel.Information).Message("list branch done").EndLog()
                 .When(e => e.In.Body is string s && s.Length > 0)   // sibling When via extension
                     .SetHeader("branch", "string")
                     .Process(e => processed.Add($"STR:{e.In.Body}"))
                 .Otherwise()                                          // sibling Otherwise via extension
                     .SetHeader("branch", "fallback")
                     .Process(e => processed.Add("FALLBACK"))
             .EndChoice()
             .Log(LogLevel.Information).Message("route complete").ShowRouteId(true).EndLog();

        var processor = route.CreateProcessor(ctx);

        var ex1 = NewExchange(new[] { "alpha", "beta", "gamma" });
        ex1.RouteId = "deep-1";
        ex1.Properties["trace"] = "T-1";
        await processor.Process(ex1);

        var ex2 = NewExchange("solo");
        ex2.RouteId = "deep-1";
        await processor.Process(ex2);

        var ex3 = NewExchange(42);
        ex3.RouteId = "deep-1";
        await processor.Process(ex3);

        processed.Should().BeEquivalentTo(new[] { "ALPHA", "BETA", "GAMMA", "STR:solo", "FALLBACK" });

        // Rich log fired at least once with the ShowRouteId / Header / Property pieces.
        var richListBranch = capture.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.Contains("opening list branch"))
            .ToList();
        richListBranch.Should().NotBeEmpty();
        var msg = richListBranch[0].Message;
        msg.Should().Contain("deep-1");      // ShowRouteId(true)
        msg.Should().Contain("branch=list");  // captured Header("branch")

        // Per-item dynamic message logged 3 times.
        capture.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("processed item="))
            .Should().Be(3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 2 — TryCatch nested inside When, RichLog inside Catch handler.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeepNest_TryCatchInsideWhen_RichLogInCatch_HandlesAndLogs()
    {
        var (ctx, capture) = NewContext();

        var route = new RouteDefinition().RouteId("deep-2");
        route.From("direct:in")
             .Choice()
                 .When(e => e.In.Body is string)
                     .TryCatch()
                         .Process(e => throw new InvalidOperationException("boom"))
                     .DoCatch<InvalidOperationException>()
                         .Log(LogLevel.Warning)
                             .Message(e => $"caught: {e.Exception?.GetType().Name}")
                             .ShowRouteId(true)
                         .EndLog()
                         .Process(e => e.In.Headers["caught"] = true)
                     .EndTryCatch()
             .EndChoice();

        var processor = route.CreateProcessor(ctx);

        var ex = NewExchange("trigger");
        ex.RouteId = "deep-2";
        await processor.Process(ex);

        ex.In.Headers["caught"].Should().Be(true);
        capture.Entries
            .Any(e => e.Level == LogLevel.Warning
                    && e.Message.Contains("caught: InvalidOperationException")
                    && e.Message.Contains("deep-2"))
            .Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 3 — Universal End() walks Parent chain through every nested scope.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeepNest_UniversalEnd_ClosesNestedScopesInOrder()
    {
        var route = new RouteDefinition();

        // 4-deep nest: Choice → When → Split → Log; close with two .End() and final .EndChoice().
        // Note: WhenDefinition.End() cascades to EndChoice(), so we use only two .End()s
        // (Log → Split, Split → When) before the explicit .EndChoice().
        var closed = route.From("direct:in")
            .Choice()
                .When(e => true)
                    .Split(e => new object?[] { 1, 2 })
                        .Log(LogLevel.Information)
                            .Message("inside")
                        .End()   // closes RichLog → SplitDefinition
                    .End()      // closes Split   → WhenDefinition
                .EndChoice();   // walks Parent chain to Choice and closes it

        // After all closers we must be back at the root RouteDefinition,
        // which means subsequent leaf-DSL calls go to the root (no exception).
        closed.SetHeader("after-close", "ok");

        // Tree shape: RouteDefinition.Outputs has 1 Choice + 1 SetHeader,
        // Choice.Whens[0].Outputs has 1 Split, Split.Outputs has 1 RichLog.
        route.Outputs.Should().HaveCount(2);
        route.Outputs[0].Should().BeOfType<ChoiceDefinition>();
        var choice = (ChoiceDefinition)route.Outputs[0];
        choice.Whens.Should().HaveCount(1);
        choice.Whens[0].Outputs.Should().HaveCount(1);
        choice.Whens[0].Outputs[0].Should().BeOfType<SplitDefinition>();
        var split = (SplitDefinition)choice.Whens[0].Outputs[0];
        split.Outputs.Should().HaveCount(1);
        split.Outputs[0].Should().BeOfType<RichLogScopeDefinition>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 4 — EndChoice() called from inside a deeply nested Log scope
    //              cascades through all open intermediate scopes (the real
    //              ergonomic regression that motivated the refactor).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeepNest_EndChoice_FromDeepInside_CascadesAllIntermediateScopes()
    {
        var route = new RouteDefinition();

        // Open 4 levels deep, then a single .EndChoice() must fast-forward through
        // RichLog + Split + When and land at the route root.
        var afterChoice = route.From("direct:in")
            .Choice()
                .When(e => true)
                    .Split(e => new object?[] { 1 })
                        .Log(LogLevel.Information)
                            .Message("deep")
                            .ShowRouteId(true)
                            .EndLog()        // close RichLog → SplitDefinition
                            .EndChoice();   // skips Split + When

        afterChoice.Should().BeAssignableTo<IRouteDefinition>();

        // We can keep chaining leaf DSL on the route root after the cascade.
        afterChoice.SetHeader("post-cascade", "ok");

        route.Outputs.Should().HaveCount(2);
        route.Outputs[0].Should().BeOfType<ChoiceDefinition>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 5 — End() from outside any scope throws a useful diagnostic.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void End_OutsideAnyScope_Throws()
    {
        var route = new RouteDefinition();
        route.From("direct:in").Process(_ => { });

        var act = () => ((IRouteDefinition)route).EndSplit();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EndSplit*outside*Split*");
    }
}
