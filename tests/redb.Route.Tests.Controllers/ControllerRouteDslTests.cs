using System.Net;
using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.Controllers.Extensions;
using redb.Route.Grpc;
using redb.Route.Http;
using redb.Route.Soap;
using redb.Route.SignalR;
using HttpDsl = redb.Route.Http.Http;
using SignalRDsl = redb.Route.SignalR.SignalR;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// The DSL methods of <see cref="ControllerRouteExtensions"/> as a route uses them: <c>From(...).RedbXController(...)</c>
/// on a started <see cref="RouteContext"/>. The generic and direct-invoke overloads run behind <c>direct:</c>; every
/// transport overload runs behind its real consumer and is called through its real producer.
/// </summary>
public class ControllerRouteDslTests
{
    private static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    private static async Task<IExchange> Call(RouteContext ctx, string uri, IExchange exchange)
    {
        var producer = ctx.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        try
        {
            await producer.Process(exchange);
            return exchange;
        }
        finally
        {
            await producer.Stop();
        }
    }

    private static IExchange Request(string method, string path, object? body = null)
    {
        var exchange = new Exchange(new Message(body));
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, method);
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, path);
        return exchange;
    }

    private static object? Reply(IExchange exchange) => (exchange.Out ?? exchange.In).Body;

    // ── Generic dispatcher ──────────────────────────────

    [Fact]
    public async Task RedbController_registry_dispatches_by_route_headers()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        registry.RegisterController(typeof(ContextsController));

        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://api").RedbController(registry));
        await ctx.Start();

        var modules = await Call(ctx, "direct://api", Request("GET", "modules/7"));
        var contexts = await Call(ctx, "direct://api", Request("GET", "contexts"));

        Reply(modules).Should().Be("module-7");
        modules.Out!.GetHeader<int>("status.code").Should().Be(200);
        Reply(contexts).Should().BeEquivalentTo(new[] { "ctx1", "ctx2" });
    }

    [Fact]
    public async Task RedbController_of_T_only_sees_that_controller()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://api").RedbController<ModulesController>());
        await ctx.Start();

        var hit = await Call(ctx, "direct://api", Request("GET", "modules"));
        var foreign = await Call(ctx, "direct://api", Request("GET", "contexts"));

        Reply(hit).Should().BeEquivalentTo(new[] { "module1", "module2" });
        foreign.Out!.GetHeader<int>("status.code").Should().Be(404);
    }

    // ── Direct invoke: RedbController<T>(methodName) ────

    [Fact]
    public async Task Direct_invoke_calls_the_named_method_and_binds_the_body()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://create").RedbController<ModulesController>(nameof(ModulesController.Create)));
        await ctx.Start();

        var exchange = await Call(ctx, "direct://create",
            new Exchange(new Message(new CreateModuleRequest { Name = "alpha" })));

        exchange.Exception.Should().BeNull();
        JsonSerializer.Serialize(Reply(exchange)).Should().Be("{\"Name\":\"alpha\",\"Created\":true}");
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
    }

    [Fact]
    public async Task Direct_invoke_ignores_the_route_headers()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://all").RedbController<ModulesController>(nameof(ModulesController.GetAll)));
        await ctx.Start();

        // The headers point at another action; the named method is what runs.
        var exchange = await Call(ctx, "direct://all", Request("DELETE", "modules/1"));

        Reply(exchange).Should().BeEquivalentTo(new[] { "module1", "module2" });
    }

    [Fact]
    public async Task Direct_invoke_awaits_a_task_and_returns_its_result()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://async").RedbController<DirectInvokeController>(nameof(DirectInvokeController.Greet)));
        await ctx.Start();

        var exchange = await Call(ctx, "direct://async", new Exchange(new Message("ann")));

        Reply(exchange).Should().Be("hello ann");
    }

    [Fact]
    public async Task Direct_invoke_of_an_async_Task_method_leaves_no_result_in_the_body()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://touch").RedbController<DirectInvokeController>(nameof(DirectInvokeController.Touch)));
        await ctx.Start();

        var exchange = await Call(ctx, "direct://touch", new Exchange(new Message("payload")));

        exchange.Exception.Should().BeNull();
        exchange.getProperty("touched").Should().Be(true);
        // A Task has no result: the body stays what the request carried, and nothing internal to the runtime
        // (the Task<VoidTaskResult> an async method really returns) leaks into it.
        Reply(exchange).Should().Be("payload");
    }

    [Fact]
    public async Task Direct_invoke_surfaces_the_action_exception_itself()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://boom").RedbController<DirectInvokeController>(nameof(DirectInvokeController.Boom)));
        await ctx.Start();

        var exchange = new Exchange(new Message("x"));
        var act = () => Call(ctx, "direct://boom", exchange);
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;

        // The route sees what the action threw, so OnException(typeof(...)) can match it; reflection's
        // TargetInvocationException wrapper is an implementation detail of the invocation.
        thrown.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("direct-boom");
    }

    [Fact]
    public async Task Direct_invoke_of_an_unknown_method_fails_with_its_name()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://missing").RedbController<DirectInvokeController>("NoSuchMethod"));
        await ctx.Start();

        var exchange = new Exchange(new Message("x"));
        var act = () => Call(ctx, "direct://missing", exchange);
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;

        thrown.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("NoSuchMethod").And.Contain(nameof(DirectInvokeController));
    }

    // ── Direct invoke by names and by expressions ───────

    [Fact]
    public async Task By_names_resolves_the_controller_and_method_from_the_registry()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        registry.RegisterController(typeof(ContextsController));

        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://named").RedbController(registry, "contexts", "list"));
        await ctx.Start();

        var exchange = await Call(ctx, "direct://named", new Exchange(new Message(null)));

        Reply(exchange).Should().BeEquivalentTo(new[] { "ctx1", "ctx2" });
        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
    }

    [Fact]
    public async Task By_names_fails_readably_for_an_unknown_pair()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://named").RedbController(registry, "modules", "nope"));
        await ctx.Start();

        var exchange = new Exchange(new Message(null));
        var act = () => Call(ctx, "direct://named", exchange);
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;

        thrown.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("'modules'").And.Contain("'nope'");
    }

    [Fact]
    public async Task By_expressions_picks_the_action_per_exchange()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        registry.RegisterController(typeof(ContextsController));

        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct://dynamic").RedbController(registry,
            e => e.In.GetHeader<string>("ctrl")!,
            e => e.In.GetHeader<string>("action")!));
        await ctx.Start();

        var first = new Exchange(new Message(null));
        first.In.setHeader("ctrl", "Modules");
        first.In.setHeader("action", "GetAll");
        var second = new Exchange(new Message(null));
        second.In.setHeader("ctrl", "Contexts");
        second.In.setHeader("action", "List");

        await Call(ctx, "direct://dynamic", first);
        await Call(ctx, "direct://dynamic", second);

        Reply(first).Should().BeEquivalentTo(new[] { "module1", "module2" });
        Reply(second).Should().BeEquivalentTo(new[] { "ctx1", "ctx2" });
    }

    // ── HTTP ─────────────────────────────────────────────

    [Fact]
    public async Task RedbHttpController_of_T_serves_real_http_requests()
    {
        var port = FreePort();
        await using var server = new SharedHttpServerManager();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new HttpComponent { ServerManager = server });
        ctx.AddRoutes(r => r.From(HttpDsl.Listen("/{**path}").Host("127.0.0.1").Port(port).InOut())
            .RedbHttpController<ModulesController>());
        await ctx.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var get = await client.GetAsync("/modules/5");
        var post = await client.PostAsync("/modules",
            new StringContent("{\"name\":\"beta\"}", Encoding.UTF8, "application/json"));
        var foreign = await client.GetAsync("/contexts");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadAsStringAsync()).Should().Be("module-5");
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        (await post.Content.ReadAsStringAsync()).Should().Be("{\"name\":\"beta\",\"created\":true}");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RedbHttpController_registry_serves_every_registered_controller()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        registry.RegisterController(typeof(ContextsController));

        var port = FreePort();
        await using var server = new SharedHttpServerManager();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new HttpComponent { ServerManager = server });
        ctx.AddRoutes(r => r.From(HttpDsl.Listen("/{**path}").Host("127.0.0.1").Port(port).InOut())
            .RedbHttpController(registry));
        await ctx.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var modules = await client.GetStringAsync("/modules");
        var status = await client.GetStringAsync("/contexts/c1/status?verbose=true");

        modules.Should().Be("[\"module1\",\"module2\"]");
        status.Should().Be("{\"name\":\"c1\",\"verbose\":true,\"status\":\"running\"}");
    }

    // ── gRPC ─────────────────────────────────────────────

    private static async Task<string> GrpcCall(RouteContext ctx, int port, string method, object? arg)
    {
        var body = arg is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(arg);
        var exchange = new Exchange(new Message(body));
        exchange.In.Headers[GrpcControllerDispatcher.MethodHeader] = method;
        await Call(ctx, GrpcDsl.Call($"127.0.0.1:{port}").Plaintext(), exchange);
        return Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
    }

    [Fact]
    public async Task RedbGrpcController_of_T_serves_real_grpc_calls()
    {
        var port = FreePort();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new GrpcComponent());
        ctx.AddRoutes(r => r.From(GrpcDsl.Listen($"127.0.0.1:{port}").InOut())
            .RedbGrpcController<EchoController>());
        await ctx.Start();

        (await GrpcCall(ctx, port, "Echo", "via-dsl")).Should().Be("echo:via-dsl");
        (await GrpcCall(ctx, port, "AsyncMethod", "x")).Should().Be("async:x");
    }

    [Fact]
    public async Task RedbGrpcController_types_and_registry_dispatch_qualified_names()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        registry.RegisterController(typeof(ContextsController));

        var typesPort = FreePort();
        var registryPort = FreePort();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new GrpcComponent());
        ctx.AddRoutes(r =>
        {
            r.From(GrpcDsl.Listen($"127.0.0.1:{typesPort}").InOut())
                .RedbGrpcController(typeof(EchoController), typeof(StatusController));
            r.From(GrpcDsl.Listen($"127.0.0.1:{registryPort}").InOut())
                .RedbGrpcController(registry);
        });
        await ctx.Start();

        (await GrpcCall(ctx, typesPort, "Status.GetAll", null)).Should().Be("status-ok");
        (await GrpcCall(ctx, typesPort, "Echo.GetAll", null)).Should().Be("[\"item1\",\"item2\"]");
        (await GrpcCall(ctx, registryPort, "Contexts.List", null)).Should().Be("[\"ctx1\",\"ctx2\"]");
    }

    // ── SignalR ──────────────────────────────────────────

    private static async Task<string?> SignalRCall(RouteContext ctx, int port, string method, object? arg)
    {
        var exchange = new Exchange(new Message(arg));
        exchange.In.setHeader(SignalRHeaders.Method, method);
        await Call(ctx, SignalRDsl.Connect($"127.0.0.1:{port}/hub").InOut(), exchange);
        return exchange.Out!.Body?.ToString();
    }

    [Fact]
    public async Task RedbSignalRController_of_T_serves_real_hub_invocations()
    {
        var port = FreePort();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SignalRComponent());
        ctx.AddRoutes(r => r.From(SignalRDsl.Hub($"127.0.0.1:{port}/hub").InOut())
            .RedbSignalRController<EchoController>());
        await ctx.Start();

        (await SignalRCall(ctx, port, "Echo", "via-dsl")).Should().Be("echo:via-dsl");
        (await SignalRCall(ctx, port, "AsyncMethod", "x")).Should().Be("async:x");
    }

    [Fact]
    public async Task RedbSignalRController_types_and_registry_dispatch_qualified_names()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        registry.RegisterController(typeof(ContextsController));

        var typesPort = FreePort();
        var registryPort = FreePort();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SignalRComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SignalRDsl.Hub($"127.0.0.1:{typesPort}/hub").InOut())
                .RedbSignalRController(typeof(EchoController), typeof(StatusController));
            r.From(SignalRDsl.Hub($"127.0.0.1:{registryPort}/hub").InOut())
                .RedbSignalRController(registry);
        });
        await ctx.Start();

        (await SignalRCall(ctx, typesPort, "Status.GetAll", null)).Should().Be("status-ok");
        (await SignalRCall(ctx, registryPort, "Modules.GetById", "9")).Should().Be("module-9");
    }

    // ── SOAP: the two overloads the SOAP suite does not reach ──

    [Fact]
    public async Task RedbSoapController_types_and_registry_dispatch_by_operation()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(SoapFareController));

        var typesPort = FreePort();
        var registryPort = FreePort();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/air").Host("127.0.0.1").Port(typesPort))
                .RedbSoapController(typeof(SoapControllerDispatcherTests.AirController));
            r.From(SoapDsl.Listen("/fare").Host("127.0.0.1").Port(registryPort))
                .RedbSoapController(registry);
            r.From("direct://air").To(SoapDsl.Call($"http://127.0.0.1:{typesPort}/air"));
            r.From("direct://fare").To(SoapDsl.Call($"http://127.0.0.1:{registryPort}/fare"));
        });
        await ctx.Start();

        var air = await Call(ctx, "direct://air",
            new Exchange(new Message("<GetFares xmlns=\"urn:air\"><Route>JFK-LHR</Route></GetFares>")));
        var fare = await Call(ctx, "direct://fare",
            new Exchange(new Message("<GetFares xmlns=\"urn:air\"><Route>JFK-LHR</Route></GetFares>")));

        air.Out!.Body!.ToString().Should().Contain("GetFaresResponse").And.Contain("<Price>100</Price>");
        fare.Out!.Body!.ToString().Should().Contain("GetFaresResponse").And.Contain("<Price>7</Price>");
    }
}
