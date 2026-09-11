using Microsoft.Extensions.Logging;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

#region Test Controllers for response-meta behavior

/// <summary>
/// Mimics a facade controller that forwards to an inner exchange whose Out already carries
/// a non-default HTTP status code (e.g. set by an OnException handler). The controller
/// propagates that code to exchange.Out before returning. Dispatcher must respect it.
/// </summary>
[Route("facade")]
public class FacadeResponseMetaController : RedbController
{
    [HttpPost("presets-503")]
    public object PresetServiceUnavailable()
    {
        Exchange.Out ??= Exchange.In.Clone();
        Exchange.Out.setHeader("redbHttp.ResponseCode", 503);
        Exchange.Out.setHeader("status.code", 503);
        return new { error = "server_error", error_description = "Database temporarily unavailable." };
    }

    [HttpPost("presets-content-type")]
    public byte[] PresetContentType()
    {
        Exchange.Out ??= Exchange.In.Clone();
        Exchange.Out.setHeader("Content-Type", "application/scim+json");
        return System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
    }

    [HttpPost("sync-throws")]
    public string SyncThrows()
    {
        // MethodInfo.Invoke wraps sync throws in TargetInvocationException.
        // Dispatcher must unwrap one level only and surface THIS message.
        throw new InvalidOperationException("sync-level error");
    }

    [HttpPost("async-throws-nested")]
    public async Task<string> AsyncThrowsNested()
    {
        await Task.Yield();
        // Async methods surface the real exception without TIE wrapping.
        // The nested InnerException is a red herring — dispatcher must NOT dereference it.
        throw new InvalidOperationException(
            "outer-real-error",
            innerException: new System.Net.Sockets.SocketException(10061));
    }
}

#endregion

public class HttpControllerDispatcherResponseMetaTests
{
    private static IExchange CreateHttpExchange(string method, string path)
    {
        var exchange = new Exchange();
        exchange.In.setHeader("redbHttp.Method", method);
        exchange.In.setHeader("redbHttp.Path", path);
        return exchange;
    }

    // ── Don't-clobber: controller-set response meta wins ─────────────────

    [Fact]
    public async Task WriteResult_Preserves_Preset_ResponseCode_503()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(FacadeResponseMetaController));
        var dispatcher = new HttpControllerDispatcher(registry, new RouteContext());

        var exchange = CreateHttpExchange("POST", "/facade/presets-503");
        await dispatcher.Process(exchange);

        // Dispatcher must respect the status the controller put on Out before returning.
        // Regression for B2: without this, inner OnException 5xx were silently masked as 200.
        exchange.Out!.GetHeader<int>("redbHttp.ResponseCode").Should().Be(503);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(503);
    }

    [Fact]
    public async Task WriteResult_Preserves_Preset_ContentType()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(FacadeResponseMetaController));
        var dispatcher = new HttpControllerDispatcher(registry, new RouteContext());

        var exchange = CreateHttpExchange("POST", "/facade/presets-content-type");
        await dispatcher.Process(exchange);

        // A controller that sets Content-Type to a non-default value (e.g. application/scim+json)
        // must not be overridden by the dispatcher's application/json default.
        exchange.Out!.Headers["Content-Type"].Should().Be("application/scim+json");
        exchange.Out!.GetHeader<int>("redbHttp.ResponseCode").Should().Be(200);
    }

    // ── Exception unwrap: TIE only, never blind InnerException ───────────

    // Which exception the dispatcher selects is still the subject here; only the place to observe it
    // moved. Since BR-4 the response carries a generic sentence, and the exception goes to the log —
    // so the log is where "the right exception was picked" is now read.

    [Fact]
    public async Task Sync_Exception_Is_Unwrapped_From_TargetInvocationException()
    {
        var (exchange, logged) = await DispatchAndCapture("/facade/sync-throws");

        exchange.Out!.GetHeader<int>("redbHttp.ResponseCode").Should().Be(500);
        logged!.Message.Should().Be("sync-level error");

        var json = System.Text.Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        json.Should().NotContain("sync-level error", "the caller gets a generic message (BR-4)");
    }

    [Fact]
    public async Task Async_Exception_Is_NOT_Unwrapped_Past_Its_InnerException()
    {
        var (exchange, logged) = await DispatchAndCapture("/facade/async-throws-nested");

        exchange.Out!.GetHeader<int>("redbHttp.ResponseCode").Should().Be(500);

        // Regression: the old `ex.InnerException ?? ex` would have surfaced the SocketException
        // (connection refused) instead of the real domain-level InvalidOperationException.
        // Async path: real exception is ex; InnerException is NOT unwrapped.
        logged!.Message.Should().Be("outer-real-error");
    }

    /// <summary>Dispatches a POST and returns the exchange together with the exception the dispatcher logged.</summary>
    private static async Task<(IExchange Exchange, Exception? Logged)> DispatchAndCapture(string path)
    {
        var capture = new ExceptionCapturingProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(FacadeResponseMetaController));
        await using var context = new RouteContext(loggerFactory: factory);
        var dispatcher = new HttpControllerDispatcher(registry, context);

        var exchange = CreateHttpExchange("POST", path);
        await dispatcher.Process(exchange);
        return (exchange, capture.Exceptions.SingleOrDefault());
    }

    private sealed class ExceptionCapturingProvider : ILoggerProvider
    {
        public List<Exception> Exceptions { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(ExceptionCapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            {
                if (ex is not null) lock (owner.Exceptions) owner.Exceptions.Add(ex);
            }
        }
    }
}
