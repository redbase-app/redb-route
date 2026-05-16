using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="RichLogProcessor"/> and the rich Log scope DSL.</summary>
public class RichLogProcessorTests
{
    // ── Processor direct tests ──

    [Fact]
    public async Task Process_StaticMessage_OutputsMessage()
    {
        var (logger, getLog) = CreateCapturingLogger();
        var processor = new RichLogProcessor(logger, LogLevel.Information,
            messages: ["hello world"],
            messageFuncs: [],
            headerNames: [],
            propertyNames: [],
            showRouteId: false);

        await processor.Process(new Exchange(new Message("body")));

        getLog().Should().Be("hello world");
    }

    [Fact]
    public async Task Process_MultipleMessages_EachOnOwnLine()
    {
        var (logger, getLog) = CreateCapturingLogger();
        var processor = new RichLogProcessor(logger, LogLevel.Information,
            messages: ["msg1", "msg2"],
            messageFuncs: [],
            headerNames: [],
            propertyNames: [],
            showRouteId: false);

        await processor.Process(new Exchange());

        getLog().Should().Be($"msg1{Environment.NewLine}msg2");
    }

    [Fact]
    public async Task Process_DynamicMessage_ExecutesFunc()
    {
        var (logger, getLog) = CreateCapturingLogger();
        var processor = new RichLogProcessor(logger, LogLevel.Warning,
            messages: [],
            messageFuncs: [ex => $"body={ex.In.Body}"],
            headerNames: [],
            propertyNames: [],
            showRouteId: false);

        await processor.Process(new Exchange(new Message("test")));

        getLog().Should().Be("body=test");
    }

    [Fact]
    public async Task Process_TemplateMessage_ResolvesPlaceholders()
    {
        var (logger, getLog) = CreateCapturingLogger();
        var processor = new RichLogProcessor(logger, LogLevel.Information,
            messages: ["Processing ${body}"],
            messageFuncs: [],
            headerNames: [],
            propertyNames: [],
            showRouteId: false);

        await processor.Process(new Exchange(new Message("order-42")));

        getLog().Should().Be("Processing order-42");
    }

    [Fact]
    public async Task Process_Headers_OutputsHeaderValues()
    {
        var (logger, getLog) = CreateCapturingLogger();
        var processor = new RichLogProcessor(logger, LogLevel.Information,
            messages: ["done"],
            messageFuncs: [],
            headerNames: ["correlationId", "source"],
            propertyNames: [],
            showRouteId: false);

        var msg = new Message("x");
        msg.Headers["correlationId"] = "abc-123";
        msg.Headers["source"] = "api";
        await processor.Process(new Exchange(msg));

        getLog().Should().Be("[h:correlationId=abc-123] [h:source=api] done");
    }

    [Fact]
    public async Task Process_Properties_OutputsPropertyValues()
    {
        var (logger, getLog) = CreateCapturingLogger();
        var processor = new RichLogProcessor(logger, LogLevel.Information,
            messages: ["ok"],
            messageFuncs: [],
            headerNames: [],
            propertyNames: ["traceId"],
            showRouteId: false);

        var exchange = new Exchange();
        exchange.Properties["traceId"] = "t-999";
        await processor.Process(exchange);

        getLog().Should().Be("[p:traceId=t-999] ok");
    }

    [Fact]
    public async Task Process_ShowRouteId_IncludesRouteIdPrefix()
    {
        var (logger, getLog) = CreateCapturingLogger();
        var processor = new RichLogProcessor(logger, LogLevel.Information,
            messages: ["hi"],
            messageFuncs: [],
            headerNames: [],
            propertyNames: [],
            showRouteId: true);

        var exchange = new Exchange();
        exchange.RouteId = "myRoute";
        await processor.Process(exchange);

        getLog().Should().Be("[rId:myRoute] hi");
    }

    [Fact]
    public async Task Process_AllCombined_OutputInCorrectOrder()
    {
        var (logger, getLog) = CreateCapturingLogger();
        var processor = new RichLogProcessor(logger, LogLevel.Debug,
            messages: ["Processing ${body}"],
            messageFuncs: [ex => $"count={ex.In.Headers.Count}"],
            headerNames: ["action"],
            propertyNames: ["step"],
            showRouteId: true);

        var msg = new Message("order");
        msg.Headers["action"] = "create";
        var exchange = new Exchange(msg);
        exchange.RouteId = "r1";
        exchange.Properties["step"] = "validate";
        await processor.Process(exchange);

        // Order: [rId:...] [p:...] [h:...] messages (each on own line) funcs
        getLog().Should().Be($"[rId:r1] [p:step=validate] [h:action=create] Processing order{Environment.NewLine}count=1");
    }

    [Fact]
    public async Task Process_MissingHeader_Skipped()
    {
        var (logger, getLog) = CreateCapturingLogger();
        var processor = new RichLogProcessor(logger, LogLevel.Information,
            messages: ["msg"],
            messageFuncs: [],
            headerNames: ["missing"],
            propertyNames: [],
            showRouteId: false);

        await processor.Process(new Exchange());

        getLog().Should().Be("msg");
    }

    [Fact]
    public async Task Process_DisabledLogLevel_DoesNothing()
    {
        var logger = new DisabledLogger();

        var processor = new RichLogProcessor(logger, LogLevel.Trace,
            messages: ["never seen"],
            messageFuncs: [],
            headerNames: [],
            propertyNames: [],
            showRouteId: false);

        await processor.Process(new Exchange());

        logger.WasCalled.Should().BeFalse();
    }

    // ── DSL scope tests ──

    [Fact]
    public async Task DSL_RichLog_MessageOnly()
    {
        var context = new RouteContext();
        IExchange? received = null;

        context.AddRoutes(r =>
        {
            r.From("direct://rich-log1")
                .Log(LogLevel.Information)
                    .Message("Step 1 done")
                    .Message("Step 2 done")
                .EndLog()
                .Process(ex => received = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://rich-log1").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        received.Should().NotBeNull();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task DSL_RichLog_WithHeadersAndProperties()
    {
        var context = new RouteContext();
        IExchange? received = null;

        context.AddRoutes(r =>
        {
            r.From("direct://rich-log2")
                .Log(LogLevel.Warning)
                    .Message("Processing exchange")
                    .Header("correlationId")
                    .Property("traceId")
                    .ShowRouteId()
                .EndLog()
                .Process(ex => received = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://rich-log2").CreateProducer();
        await producer.Start();

        var msg = new Message("payload");
        msg.Headers["correlationId"] = "c-1";
        var ex = new Exchange(msg);
        ex.Properties["traceId"] = "t-1";
        await producer.Process(ex);

        received.Should().NotBeNull();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task DSL_RichLog_WithDynamicMessage()
    {
        var context = new RouteContext();
        IExchange? received = null;

        context.AddRoutes(r =>
        {
            r.From("direct://rich-log3")
                .Log(LogLevel.Error)
                    .Message(ex => $"body={ex.In.Body}")
                    .Message("static after dynamic")
                .EndLog()
                .Process(ex => received = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://rich-log3").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test-body")));

        received.Should().NotBeNull();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task DSL_RichLog_EndAlsoWorks()
    {
        var context = new RouteContext();
        IExchange? received = null;

        context.AddRoutes(r =>
        {
            r.From("direct://rich-log4")
                .Log(LogLevel.Debug)
                    .Message("via End()")
                .End() // End() instead of EndLog()
                .Process(ex => received = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://rich-log4").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        received.Should().NotBeNull();
        await context.DisposeAsync();
    }

    [Fact]
    public void DSL_RichLog_RecordsRichLogStep()
    {
        var def = new RouteDefinition();
        def.From("direct://test")
            .Log(LogLevel.Warning)
                .Message("msg1")
                .Message(ex => "dynamic")
                .Header("h1")
                .Property("p1")
                .ShowRouteId()
            .EndLog();

        var step = def.Steps.OfType<RichLogStep>().Single();
        step.Level.Should().Be(LogLevel.Warning);
        step.Messages.Should().ContainSingle().Which.Should().Be("msg1");
        step.MessageFuncs.Should().HaveCount(1);
        step.HeaderNames.Should().ContainSingle().Which.Should().Be("h1");
        step.PropertyNames.Should().ContainSingle().Which.Should().Be("p1");
        step.ShowRouteId.Should().BeTrue();
    }

    [Fact]
    public void DSL_Message_OutsideScope_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.From("direct://test").Message("oops");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Log() scope*");
    }

    [Fact]
    public void DSL_Header_OutsideScope_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.From("direct://test").Header("x");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Log() scope*");
    }

    [Fact]
    public void DSL_Property_OutsideScope_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.From("direct://test").Property("x");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Log() scope*");
    }

    [Fact]
    public void DSL_ShowRouteId_OutsideScope_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.From("direct://test").ShowRouteId();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Log() scope*");
    }

    [Fact]
    public void DSL_EndLog_OutsideScope_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.From("direct://test").EndLog();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Log() scope*");
    }

    // ── Simple Log one-liners still work ──

    [Fact]
    public async Task SimpleLog_StillWorks()
    {
        var context = new RouteContext();
        IExchange? received = null;

        context.AddRoutes(r =>
        {
            r.From("direct://simple-log")
                .Log("simple message")
                .Log(ex => $"dynamic {ex.In.Body}")
                .Log("template ${body}")
                .Process(ex => received = ex);
        });

        await context.Start();
        var producer = context.GetEndpoint("direct://simple-log").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        received.Should().NotBeNull();
        await context.DisposeAsync();
    }

    // ── Helper ──

    /// <summary>
    /// Simple in-memory logger that captures the formatted message string.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public string? LastMessage { get; private set; }
        public LogLevel? LastLevel { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LastLevel = logLevel;
            LastMessage = formatter(state, exception);
        }
    }

    private sealed class DisabledLogger : ILogger
    {
        public bool WasCalled { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            WasCalled = true;
        }
    }

    private static (CapturingLogger logger, Func<string?> getLastMessage) CreateCapturingLogger()
    {
        var logger = new CapturingLogger();
        return (logger, () => logger.LastMessage);
    }
}
