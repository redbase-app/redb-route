using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Extensions;
using redb.Route.Grpc;
using redb.Route.Http;
using HttpDsl = redb.Route.Http.Http;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// What the package README promises about controllers themselves: an instance per request created by
/// <c>Activator.CreateInstance()</c> with no constructor injection, and one controller class serving any transport.
/// </summary>
public class ReadmePromiseTests
{
    private static IExchange Get(string path)
    {
        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, path);
        return exchange;
    }

    [Fact]
    public async Task Every_request_gets_its_own_controller_instance_bound_to_its_own_exchange()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(InstanceProbeController));
        await using var context = new RouteContext();
        var dispatcher = new ControllerDispatcherProcessor(registry, context);

        var first = Get("instance");
        var second = Get("instance");
        await dispatcher.Process(first);
        await dispatcher.Process(second);

        var (firstInstance, firstExchange) = Split(first.Out!.Body);
        var (secondInstance, secondExchange) = Split(second.Out!.Body);

        firstInstance.Should().NotBe(secondInstance, "the same dispatcher must not reuse a controller");
        firstExchange.Should().Be(first.ExchangeId);
        secondExchange.Should().Be(second.ExchangeId);

        static (string Instance, string Exchange) Split(object? body)
        {
            var parts = ((string)body!).Split('|');
            return (parts[0], parts[1]);
        }
    }

    [Fact]
    public async Task A_controller_with_constructor_parameters_is_not_injected_and_fails_as_a_500()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ConstructorArgumentController));
        await using var context = new RouteContext();

        var exchange = Get("ctor-arg");
        await new ControllerDispatcherProcessor(registry, context).Process(exchange);

        // No DI: Activator.CreateInstance needs a parameterless constructor. The failure is the dispatcher's
        // generic 500, not a half-built controller.
        exchange.Out!.GetHeader<int>("status.code").Should().Be(500);
        exchange.Out.Body.Should().BeOfType<ControllerErrorResponse>();
    }

    [Fact]
    public async Task One_controller_class_serves_http_and_grpc_in_one_context()
    {
        var httpPort = global::redb.Route.Tests.Shared.TestPorts.Next();
        var grpcPort = global::redb.Route.Tests.Shared.TestPorts.Next();

        await using var server = new SharedHttpServerManager();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new HttpComponent { ServerManager = server });
        ctx.AddComponent(new GrpcComponent());
        ctx.AddRoutes(r =>
        {
            r.From(HttpDsl.Listen("/{**path}").Host("127.0.0.1").Port(httpPort).InOut())
                .RedbHttpController<ModulesController>();
            r.From(GrpcDsl.Listen($"127.0.0.1:{grpcPort}").InOut())
                .RedbGrpcController<ModulesController>();
        });
        await ctx.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") };
        var viaHttp = await client.GetStringAsync("/modules/7");

        var grpc = new Exchange(new Message(JsonSerializer.SerializeToUtf8Bytes("7")));
        grpc.In.Headers[GrpcControllerDispatcher.MethodHeader] = nameof(ModulesController.GetById);
        var producer = ctx.GetEndpoint(GrpcDsl.Call($"127.0.0.1:{grpcPort}").Plaintext()).CreateProducer();
        await producer.Start();
        try { await producer.Process(grpc); }
        finally { await producer.Stop(); }
        var viaGrpc = Encoding.UTF8.GetString((byte[])grpc.Out!.Body!);

        // The same action, the same answer; only the transport differs.
        viaHttp.Should().Be("module-7");
        viaGrpc.Should().Be("module-7");
    }
}
