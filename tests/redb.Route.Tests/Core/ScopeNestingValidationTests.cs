using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for the generic scope-nesting validation mechanism (ICompositeScope / IDurableScope /
/// IScopeNestingRule), exercised through the replay checkpoint constraint: a checkpoint may not
/// cross a branching composite (build error, §6) and warns inside a durable transaction (§10).
/// </summary>
public class ScopeNestingValidationTests
{
    private static RouteContext New() => new("nesting-test");

    [Fact]
    public async Task Checkpoint_InsideFilter_FailsBuild()
    {
        var ctx = New();
        ctx.AddRoutes(r => r
            .From("direct:f").RouteId("f")
            .Filter(_ => true)
                .Replayable("m", b => b.Process(_ => { }))
            .EndFilter()
            .To("direct:sink"));

        var act = () => ctx.Start();
        (await act.Should().ThrowAsync<Exception>()).Which.Message
            .Should().Contain("cannot cross").And.Contain("FilterDefinition");
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Checkpoint_InsideChoiceBranch_FailsBuild()
    {
        var ctx = New();
        ctx.AddRoutes(r => r
            .From("direct:c").RouteId("c")
            .Choice()
                .When(_ => true)
                    .Replayable("m", b => b.Process(_ => { }))
                .EndChoice()
            .To("direct:sink"));

        var act = () => ctx.Start();
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("cannot cross");
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Checkpoint_InsideSplit_FailsBuild()
    {
        var ctx = New();
        ctx.AddRoutes(r => r
            .From("direct:s").RouteId("s")
            .Split(ex => new object?[] { 1, 2 })
                .Replayable("m", b => b.Process(_ => { }))
            .EndSplit());

        var act = () => ctx.Start();
        (await act.Should().ThrowAsync<Exception>()).Which.Message
            .Should().Contain("cannot cross").And.Contain("SplitDefinition");
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Checkpoint_InsideTransaction_Warns_ButBuilds()
    {
        var warnings = new List<string>();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(warnings)));
        var ctx = new RouteContext("tx", loggerFactory);

        ctx.AddRoutes(r => r
            .From("direct:tx").RouteId("tx")
            .Transaction()
                .Replayable("m", b => b.Process(_ => { }))
            .EndTransaction());

        await ctx.Start();   // must NOT throw

        ctx.GetReplayMarkers().Should().Contain(("tx", "m"));
        warnings.Should().Contain(w => w.Contains("runs") && w.Contains("OUTSIDE that transaction"));
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Checkpoint_TopLevel_IsAllowed()
    {
        var ctx = New();
        ctx.AddRoutes(r => r
            .From("direct:ok").RouteId("ok")
            .Replayable("m").Process(_ => { }));

        var act = () => ctx.Start();
        await act.Should().NotThrowAsync();
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task NestedCheckpoints_AreAllowed()
    {
        var ctx = New();
        ctx.AddRoutes(r => r
            .From("direct:nest").RouteId("nest")
            .Replayable("outer")
                .Process(_ => { })
                .Replayable("inner", b => b.Process(_ => { })));

        var act = () => ctx.Start();
        await act.Should().NotThrowAsync();
        ctx.GetReplayMarkers().Select(m => m.MarkerName).Should().Contain(new[] { "outer", "inner" });
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
