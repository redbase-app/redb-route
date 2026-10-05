using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// A <c>Catch&lt;T&gt;</c> / <c>OnException&lt;T&gt;</c> on a type that is an
/// <see cref="OperationCanceledException"/> can never fire — a genuine cancellation is never handled and a
/// timeout arrives as <see cref="TimeoutException"/> — so the route load warns about it instead of leaving
/// the dead branch to be discovered by a red test.
/// </summary>
public class CancellationHandlerValidationTests
{
    private static (RouteContext Context, List<string> Warnings) New()
    {
        var warnings = new List<string>();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(warnings)));
        return (new RouteContext("cancellation-handler", loggerFactory), warnings);
    }

    [Fact]
    public async Task TryCatch_Catch_OperationCanceled_Warns()
    {
        var (ctx, warnings) = New();
        ctx.AddRoutes(r => r
            .From("direct:c").RouteId("c")
            .TryCatch()
                .Process(_ => { })
            .Catch<TaskCanceledException>()
                .Process(_ => { })
            .EndTryCatch()
            .To("direct:sink"));

        await ctx.Start(); // the route still builds — the branch is dead, not illegal

        warnings.Should().Contain(w => w.Contains("TaskCanceledException") && w.Contains("can never fire"));
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task TryCatch_Catch_TimeoutException_DoesNotWarn()
    {
        var (ctx, warnings) = New();
        ctx.AddRoutes(r => r
            .From("direct:t").RouteId("t")
            .TryCatch()
                .Process(_ => { })
            .Catch<TimeoutException>()
                .Process(_ => { })
            .EndTryCatch()
            .To("direct:sink"));

        await ctx.Start();

        warnings.Should().NotContain(w => w.Contains("can never fire"));
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task OnException_OperationCanceled_Warns()
    {
        var (ctx, warnings) = New();
        ctx.AddRoutes(r =>
        {
            r.OnException<OperationCanceledException>().Handled();
            r.From("direct:o").RouteId("o").Process(_ => { }).To("direct:sink");
        });

        await ctx.Start();

        warnings.Should().Contain(w => w.Contains("OperationCanceledException") && w.Contains("can never fire"));
        await ctx.DisposeAsync();
    }

    private sealed class CapturingLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) sink.Add(formatter(state, exception));
            }
        }
    }
}
