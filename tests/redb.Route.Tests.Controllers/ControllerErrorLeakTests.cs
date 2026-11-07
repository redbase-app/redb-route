using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// BR-4 (`redb.Tsak/docs/BOUNDARIES_AND_FOLLOWUPS.md` §1): an unhandled exception in an action used to
/// travel to the caller as its own text, so a path, a connection string or an internal service name
/// left the process through an HTTP response — and it was written nowhere else, so the operator could
/// not even see what the caller saw. The caller gets a generic message; the detail goes to the log.
/// </summary>
public class ControllerErrorLeakTests
{
    private const string Secret = "Server=db-prod;Password=hunter2;Database=payments";

    [Route("leaky")]
    public class LeakyController : RedbController
    {
        /// <summary>Sync path: MethodInfo.Invoke wraps the exception in a TargetInvocationException.</summary>
        [HttpGet("sync")]
        public string Sync() => throw new InvalidOperationException(Secret);

        /// <summary>Async path: the faulted Task rethrows the original exception, no TIE.</summary>
        [HttpGet("async")]
        public async Task<string> Async()
        {
            await Task.Yield();
            throw new InvalidOperationException(Secret);
        }
    }

    [Theory]
    [InlineData("leaky/sync")]
    [InlineData("leaky/async")]
    public async Task AnUnhandledException_NeverReachesTheCaller(string path)
    {
        var (exchange, _) = await Dispatch(path);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.GetHeader<int>("status.code").Should().Be(500);

        var error = exchange.Out.Body.Should().BeOfType<ControllerErrorResponse>().Subject;
        error.Message.Should().NotContain("hunter2", "a caller must never receive the exception's own text");
        error.Message.Should().NotContain("Password");
        error.Message.Should().NotBeEmpty("the caller still needs to know the request failed");
        error.Error.Should().Be("InternalError");
    }

    [Theory]
    [InlineData("leaky/sync")]
    [InlineData("leaky/async")]
    public async Task TheDetailIsLogged_SoItIsNotLostAltogether(string path)
    {
        var (_, log) = await Dispatch(path);

        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Exception!.Message.Should().Contain("hunter2", "the operator must be able to see what actually failed");
    }

    // The report named one dispatcher. There are five, and four run user action code the same way — each
    // had its own copy of "return the exception's Message". The fifth, SOAP, rethrows instead, so its
    // fault is written by SoapConsumer; that path leaked the same way and is fixed there (SoapLoopbackTests).

    [Fact]
    public async Task HttpDispatcher_DoesNotLeakEither()
    {
        var log = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(LeakyController));
        await using var context = new RouteContext(loggerFactory: factory);
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader("redbHttp.Method", "GET");
        exchange.In.setHeader("redbHttp.Path", "/leaky/sync");
        await dispatcher.Process(exchange);

        var json = System.Text.Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        json.Should().NotContain("hunter2").And.NotContain("Password");
        exchange.Out.GetHeader<int>("status.code").Should().Be(500);
        log.Entries.Should().Contain(e => e.Exception != null && e.Exception.Message.Contains("hunter2"));
    }

    [Route("grpc-leaky")]
    public class GrpcLeakyController : RedbController
    {
        [HttpPost("boom")]
        public string Boom() => throw new InvalidOperationException(Secret);
    }

    [Fact]
    public async Task GrpcDispatcher_DoesNotLeakEither()
    {
        var log = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        await using var context = new RouteContext(loggerFactory: factory);
        var dispatcher = new GrpcControllerDispatcher(context, typeof(GrpcLeakyController));

        var exchange = new Exchange();
        exchange.In.setHeader(GrpcControllerDispatcher.MethodHeader, "Boom");
        await dispatcher.Process(exchange);

        var json = System.Text.Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        json.Should().NotContain("hunter2").And.NotContain("Password");
        log.Entries.Should().Contain(e => e.Exception != null && e.Exception.Message.Contains("hunter2"));
    }

    [Fact]
    public async Task SignalRDispatcher_DoesNotLeakEither()
    {
        var log = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        await using var context = new RouteContext(loggerFactory: factory);
        var dispatcher = new SignalRControllerDispatcher(context, typeof(GrpcLeakyController));

        var exchange = new Exchange();
        exchange.In.setHeader("redbSignalR.Method", "Boom");
        await dispatcher.Process(exchange);

        var error = exchange.Out!.Body.Should().BeOfType<ControllerErrorResponse>().Subject;
        error.Message.Should().NotContain("hunter2").And.NotContain("Password");
        log.Entries.Should().Contain(e => e.Exception != null && e.Exception.Message.Contains("hunter2"));
    }

    // Withholding the detail is only defensible because the caller can quote a reference and an operator
    // can find it. That correlation is the compensating control, so it is pinned here: without it a "fix"
    // returning a bare "Error." would satisfy every leak assertion above.

    [Fact]
    public async Task TheCallerGetsAReference_AndTheSameReferenceIsInTheLog()
    {
        var (exchange, log) = await Dispatch("leaky/sync");

        var error = (ControllerErrorResponse)exchange.Out!.Body!;
        error.Message.Should().Contain(exchange.ExchangeId, "the caller must have something to quote");

        var line = log.Entries.Should().ContainSingle(e => e.Exception != null).Subject;
        line.Message.Should().Contain(exchange.ExchangeId, "the operator finds the detail by the id the caller quotes");
    }

    [Fact]
    public async Task TheLoggerIsFoundWhenLoggingComesFromDependencyInjection()
    {
        // IRouteContext.GetService<T>() reads the context's own table, not the container. A host that
        // does AddLogging() and `new RouteContext(serviceProvider)` must still get the detail — otherwise
        // hiding it from the caller means losing it, which is worse than the disclosure it replaced.
        var log = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => { b.SetMinimumLevel(LogLevel.Debug); b.AddProvider(log); });
        using var provider = services.BuildServiceProvider();

        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(LeakyController));
        await using var context = new RouteContext(provider);
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "leaky/sync");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");
        await dispatcher.Process(exchange);

        log.Entries.Should().Contain(e => e.Exception != null && e.Exception.Message.Contains("hunter2"));
    }

    [Route("nested")]
    public class NestedThrowController : RedbController
    {
        /// <summary>Async: the real failure wraps a transient one. Only TIE may be unwrapped, never this.</summary>
        [HttpGet("async")]
        public async Task<string> Async()
        {
            await Task.Yield();
            throw new InvalidOperationException("outer-real-error", new System.Net.Sockets.SocketException(10061));
        }
    }

    [Theory]
    [InlineData("grpc")]
    [InlineData("signalr")]
    public async Task TheLoggedExceptionIsTheOneTheActionReported_NotADeeperWrapper(string transport)
    {
        var log = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        await using var context = new RouteContext(loggerFactory: factory);

        var exchange = new Exchange();
        if (transport == "grpc")
        {
            exchange.In.setHeader(GrpcControllerDispatcher.MethodHeader, "Async");
            await new GrpcControllerDispatcher(context, typeof(NestedThrowController)).Process(exchange);
        }
        else
        {
            exchange.In.setHeader("redbSignalR.Method", "Async");
            await new SignalRControllerDispatcher(context, typeof(NestedThrowController)).Process(exchange);
        }

        var logged = log.Entries.Should().ContainSingle(e => e.Exception != null).Subject.Exception!;
        logged.Message.Should().Be("outer-real-error",
            "`ex.InnerException ?? ex` would log the SocketException — and the log is now the only record");
    }

    private sealed class ThrowingFilter : IControllerActionFilter
    {
        public int Order => 0;
        public Task BeforeAsync(ControllerActionContext context, CancellationToken ct) => throw new InvalidOperationException("filter-before-failed");
        public Task AfterAsync(ControllerActionContext context, CancellationToken ct) => throw new InvalidOperationException("filter-after-failed");
    }

    [Route("ok")]
    public class OkController : RedbController
    {
        [HttpGet] public string Get() => "fine";
    }

    [Fact]
    public async Task AFilterThatThrows_IsLogged_AndDispatchStillSucceeds()
    {
        var log = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(OkController));
        await using var context = new RouteContext(loggerFactory: factory);
        var dispatcher = new ControllerDispatcherProcessor(registry, context, [new ThrowingFilter()]);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, "ok");
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");
        await dispatcher.Process(exchange);

        exchange.Out!.Body.Should().Be("fine", "a filter never breaks dispatch");
        // IControllerActionFilter documents "logged but does not propagate"; a bare catch made a filter
        // failing on every request indistinguishable from one that works.
        log.Entries.Where(e => e.Exception is not null).Select(e => e.Exception!.Message)
            .Should().Contain("filter-before-failed").And.Contain("filter-after-failed");
    }

    // The other half of the rule: errors the framework writes itself stay descriptive. Without this the
    // leak tests alone would be satisfied by genericising 400 and 404 too, which would be a usability
    // regression dressed as a security fix.

    [Fact]
    public async Task FrameworkErrorsKeepTheirText()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(LeakyController));
        await using var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var missingHeaders = new Exchange();
        await dispatcher.Process(missingHeaders);
        var bad = (ControllerErrorResponse)missingHeaders.Out!.Body!;
        bad.StatusCode.Should().Be(400);
        bad.Message.Should().Contain(ControllerDispatcherProcessor.PathHeader, "the caller must learn which header is missing");

        var noAction = new Exchange();
        noAction.In.setHeader(ControllerDispatcherProcessor.PathHeader, "nothing/here");
        noAction.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");
        await dispatcher.Process(noAction);
        var notFound = (ControllerErrorResponse)noAction.Out!.Body!;
        notFound.StatusCode.Should().Be(404);
        notFound.Message.Should().Contain("nothing/here", "the caller must learn what did not match");
    }

    private static async Task<(Exchange Exchange, RecordingLoggerProvider Log)> Dispatch(string path)
    {
        var log = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Debug); b.AddProvider(log); });

        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(LeakyController));
        await using var context = new RouteContext(loggerFactory: factory);
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, path);
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");
        await dispatcher.Process(exchange);
        return (exchange, log);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recording(this);
        public void Dispose() { }

        private sealed class Recording(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) owner.Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
