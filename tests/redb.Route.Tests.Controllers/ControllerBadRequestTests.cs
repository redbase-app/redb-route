using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.Core;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// A request the dispatcher cannot bind is the CALLER's error, not the server's. Every dispatcher
/// used to funnel binding failures — a JSON body missing required members, a route parameter that
/// is not a guid — into the generic <c>catch (Exception)</c>: the caller got 500 "InternalError"
/// and the operator got an Error log with a stack for every piece of junk POSTed from outside.
/// <para>
/// The boundary is drawn around parameter RESOLUTION, not around exception types: a
/// <see cref="FormatException"/> thrown while binding is a 400, the same exception thrown inside
/// the action stays a 500 — the twin tests below pin both sides.
/// </para>
/// </summary>
public sealed class ControllerBadRequestTests : IAsyncLifetime
{
    // Owned by the test instance (xUnit makes one per test) and released after it: the dispatcher the factory
    // below returns keeps using both.
    private readonly RouteContext _httpContext = new();
    private ILoggerFactory? _loggerFactory;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _httpContext.DisposeAsync();
        _loggerFactory?.Dispose();
    }

    // ── Test surface ──

    public sealed class StrictOrderRequest
    {
        public required string ClientId { get; set; }
        public required string CallbackUrl { get; set; }
    }

    public sealed class OrdersController : RedbController
    {
        [HttpPost]
        public object Create([FromBody] StrictOrderRequest request) => new { request.ClientId };

        [HttpGet("{id}")]
        public string GetById([FromRoute("id")] Guid id) => $"order-{id}";

        [HttpGet]
        public string Explode() => throw new FormatException("the action itself is broken");
    }

    private sealed class CapturingLogs : ILoggerProvider, ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Lines = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
        public void Dispose() { }
    }

    private (HttpControllerDispatcher Dispatcher, CapturingLogs Logs) CreateHttpDispatcher()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(OrdersController));

        var logs = new CapturingLogs();
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        _httpContext.AddService(typeof(ILoggerFactory), _loggerFactory);

        return (new HttpControllerDispatcher(registry, _httpContext), logs);
    }

    private static IExchange HttpExchange(string method, string path, string? jsonBody = null,
        Dictionary<string, string>? routeParams = null)
    {
        var exchange = new Exchange(new Message());
        if (jsonBody is not null)
            exchange.In.Body = Encoding.UTF8.GetBytes(jsonBody);
        exchange.In.setHeader("redbHttp.Method", method);
        exchange.In.setHeader("redbHttp.Path", path);
        if (routeParams is not null)
            foreach (var (k, v) in routeParams)
                exchange.In.setHeader($"redbHttp.RouteParam.{k}", v);
        return exchange;
    }

    private static (int Status, string Error, string Message) ReadError(IExchange exchange)
    {
        exchange.Out.Should().NotBeNull();
        // Existing per-transport shapes this test set does not take a position on: HTTP writes
        // camelCase bytes, gRPC PascalCase bytes, SignalR the response object itself (its
        // transport serializes later).
        if (exchange.Out!.Body is ControllerErrorResponse direct)
            return (direct.StatusCode, direct.Error, direct.Message);
        var doc = JsonDocument.Parse((byte[])exchange.Out!.Body!);
        JsonElement Prop(string name) =>
            doc.RootElement.TryGetProperty(name, out var v)
                ? v
                : doc.RootElement.GetProperty(char.ToUpperInvariant(name[0]) + name[1..]);
        return (Prop("statusCode").GetInt32(), Prop("error").GetString()!, Prop("message").GetString()!);
    }

    // ── HTTP: the paste's exact case ──

    [Fact]
    public async Task A_body_missing_required_members_is_the_callers_400_not_our_500()
    {
        var (dispatcher, logs) = CreateHttpDispatcher();
        var exchange = HttpExchange("POST", "/orders", jsonBody: "{}");

        await dispatcher.Process(exchange);

        var (status, error, message) = ReadError(exchange);
        status.Should().Be(400, "a client that sent an unbindable body must not be told the server broke");
        error.Should().Be("BadRequest");
        message.Should().NotContain("unexpected error", "the caller gets told what is wrong with THEIR request");

        logs.Lines.Should().Contain(l => l.Level == LogLevel.Warning,
            "malformed input is an expected event class, logged as a warning");
        logs.Lines.Should().NotContain(l => l.Level == LogLevel.Error,
            "junk POSTed from outside must not fill the operator's log with error stacks");
    }

    [Fact]
    public async Task A_route_param_that_is_not_a_guid_is_a_400_too()
    {
        var (dispatcher, _) = CreateHttpDispatcher();
        var exchange = HttpExchange("GET", "/orders/abc",
            routeParams: new() { ["id"] = "abc" });

        await dispatcher.Process(exchange);

        ReadError(exchange).Status.Should().Be(400,
            "binding covers route and query parameters, not just the JSON body");
    }

    [Fact]
    public async Task The_same_exception_inside_the_action_stays_a_500()
    {
        // The boundary is the resolution step, not the exception type: FormatException thrown by
        // the action is the server's own failure and keeps the 500 + Error-with-stack contract.
        var (dispatcher, logs) = CreateHttpDispatcher();
        var exchange = HttpExchange("GET", "/orders");

        await dispatcher.Process(exchange);

        var (status, error, _) = ReadError(exchange);
        status.Should().Be(500);
        error.Should().Be("InternalError");
        logs.Lines.Should().Contain(l => l.Level == LogLevel.Error);
    }

    // ── gRPC ──

    [Fact]
    public async Task Grpc_malformed_json_body_is_a_400()
    {
        await using var context = new RouteContext();
        var dispatcher = new GrpcControllerDispatcher(context, typeof(OrdersController));

        var exchange = new Exchange(new Message(Encoding.UTF8.GetBytes("{ not json")));
        exchange.In.setHeader("dispatch-method", "Orders.Create");

        await dispatcher.Process(exchange);

        ReadError(exchange).Status.Should().Be(400);
    }

    // ── SOAP ──

    [System.Xml.Serialization.XmlRoot("StrictOrder", Namespace = "urn:orders")]
    public class StrictOrder { public string ClientId { get; set; } = ""; }

    public sealed class SoapOrdersController : RedbController
    {
        public string Register([FromBody] StrictOrder order) => order.ClientId;
    }

    [Fact]
    public async Task Soap_junk_xml_surfaces_as_MalformedRequestException_not_a_generic_failure()
    {
        // SOAP has no status codes; the dispatcher's "your request is wrong" is the typed
        // exception the SOAP consumer maps to a Sender fault. An unhandled XmlSerializer
        // exception became a Receiver fault with a generic text — a retry hint for bytes that
        // will fail identically forever.
        await using var context = new RouteContext();
        var dispatcher = new SoapControllerDispatcher(context, typeof(SoapOrdersController));
        var exchange = new Exchange(new Message("<this is not xml"));
        exchange.In.setHeader(SoapControllerDispatcher.OperationHeader, "Register");

        var act = () => dispatcher.Process(exchange);

        await act.Should().ThrowAsync<MalformedRequestException>();
    }

    // -- A simple value that does not convert is a binding error (Camel: ParameterBindingException) --------

    private HttpControllerDispatcher BindingDispatcher()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(BindingController));
        return new HttpControllerDispatcher(registry, _httpContext);
    }

    [Fact]
    public async Task A_query_value_that_is_not_a_number_is_a_400_naming_the_parameter_and_the_value()
    {
        var exchange = HttpExchange("GET", "/binding/query");
        exchange.In.setHeader("redbHttp.QueryParam.page", "abc");

        await BindingDispatcher().Process(exchange);

        var (status, error, message) = ReadError(exchange);
        status.Should().Be(400, "page=abc is not a page number; binding it as 0 answered a different question");
        error.Should().Be("BadRequest");
        message.Should().Contain("page").And.Contain("abc");
    }

    [Fact]
    public async Task A_header_value_that_is_not_a_number_is_a_400()
    {
        var exchange = HttpExchange("GET", "/binding/header");
        exchange.In.setHeader("X-Limit", "lots");

        await BindingDispatcher().Process(exchange);

        var (status, _, message) = ReadError(exchange);
        status.Should().Be(400);
        message.Should().Contain("X-Limit").And.Contain("lots");
    }

    [Fact]
    public async Task SignalR_an_argument_that_is_not_a_number_is_a_400()
    {
        await using var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(EchoController));
        var exchange = new Exchange(new Message("abc"));
        exchange.In.setHeader("redbSignalR.Method", nameof(EchoController.GetById));

        await dispatcher.Process(exchange);

        var (status, _, message) = ReadError(exchange);
        status.Should().Be(400);
        message.Should().Contain("id").And.Contain("abc");
    }

    // -- Generic dispatcher: the same 400/500 boundary as the transport dispatchers --------------------------

    private static IExchange GenericExchange(string method, string path)
    {
        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, method);
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, path);
        return exchange;
    }

    [Fact]
    public async Task Generic_a_route_param_that_is_not_a_guid_is_a_400()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(OrdersController));
        var exchange = GenericExchange("GET", "orders/abc");

        await new ControllerDispatcherProcessor(registry, _httpContext).Process(exchange);

        var (status, error, message) = ReadError(exchange);
        status.Should().Be(400);
        error.Should().Be("BadRequest");
        message.Should().NotContain("unexpected error");
    }

    [Fact]
    public async Task Generic_a_query_value_that_is_not_a_number_is_a_400()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(BindingController));
        var exchange = GenericExchange("GET", "binding/query");
        exchange.In.setHeader("query.page", "abc");

        await new ControllerDispatcherProcessor(registry, _httpContext).Process(exchange);

        var (status, _, message) = ReadError(exchange);
        status.Should().Be(400);
        message.Should().Contain("page").And.Contain("abc");
    }

    [Fact]
    public async Task Generic_the_same_exception_inside_the_action_stays_a_500()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(OrdersController));
        var exchange = GenericExchange("GET", "orders");

        await new ControllerDispatcherProcessor(registry, _httpContext).Process(exchange);

        var (status, error, _) = ReadError(exchange);
        status.Should().Be(500);
        error.Should().Be("InternalError");
    }

    // ── SignalR ──

    [Fact]
    public async Task SignalR_unbindable_args_are_a_400()
    {
        await using var context = new RouteContext();
        var dispatcher = new SignalRControllerDispatcher(context, typeof(OrdersController));

        var exchange = new Exchange(new Message("{ definitely not bindable"));
        exchange.In.setHeader("redbSignalR.Method", "Orders.Create");

        await dispatcher.Process(exchange);

        ReadError(exchange).Status.Should().Be(400);
    }
}
