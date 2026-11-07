using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// What an action returns reaches the reply the same way on every dispatcher. The README promises
/// "<c>null</c> or <c>Task</c> (void) → 204, no body"; an <c>async Task</c> method really returns a
/// <c>Task&lt;VoidTaskResult&gt;</c>, and a <c>ValueTask</c>/<c>ValueTask&lt;T&gt;</c> is a struct that is not a
/// <see cref="Task"/> at all, so both shapes are pinned here per dispatcher.
/// </summary>
public class ActionReturnShapeTests
{
    private static IExchange Generic(string method, string path)
    {
        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, method);
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, path);
        return exchange;
    }

    private static IExchange Http(string method, string path)
    {
        var exchange = new Exchange(new Message(Array.Empty<byte>()));
        exchange.In.setHeader(HttpControllerDispatcher.HttpMethodHeader, method);
        exchange.In.setHeader(HttpControllerDispatcher.HttpPathHeader, path);
        return exchange;
    }

    private static IExchange ByName(string header, string name)
    {
        var exchange = new Exchange();
        exchange.In.setHeader(header, name);
        return exchange;
    }

    private static ControllerRegistry Registry()
    {
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ReturnShapesController));
        return registry;
    }

    // ── async Task: no result ───────────────────────────

    [Fact]
    public async Task Generic_async_Task_is_204_without_a_body()
    {
        await using var context = new RouteContext();
        var exchange = Generic("POST", "shapes/done");

        await new ControllerDispatcherProcessor(Registry(), context).Process(exchange);

        exchange.getProperty("done").Should().Be(true);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task Http_async_Task_is_204_without_a_body()
    {
        await using var context = new RouteContext();
        var exchange = Http("POST", "/shapes/done");

        await new HttpControllerDispatcher(Registry(), context).Process(exchange);

        exchange.getProperty("done").Should().Be(true);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.GetHeader<int>(HttpControllerDispatcher.HttpResponseCodeHeader).Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task SignalR_async_Task_is_204_without_a_body()
    {
        await using var context = new RouteContext();
        var exchange = ByName("redbSignalR.Method", nameof(ReturnShapesController.Done));

        await new SignalRControllerDispatcher(context, typeof(ReturnShapesController)).Process(exchange);

        exchange.getProperty("done").Should().Be(true);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task Grpc_async_Task_is_204_without_a_body()
    {
        await using var context = new RouteContext();
        var exchange = ByName(GrpcControllerDispatcher.MethodHeader, nameof(ReturnShapesController.Done));

        await new GrpcControllerDispatcher(context, typeof(ReturnShapesController)).Process(exchange);

        exchange.getProperty("done").Should().Be(true);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task Soap_async_Task_answers_an_empty_body()
    {
        await using var context = new RouteContext();
        var exchange = ByName(SoapControllerDispatcher.OperationHeader, nameof(ReturnShapesController.Done));

        await new SoapControllerDispatcher(context, typeof(ReturnShapesController)).Process(exchange);

        exchange.getProperty("done").Should().Be(true);
        exchange.Out!.Body.Should().Be(string.Empty);
    }

    // -- void: the request body never comes back as the reply --------------------------------------------

    [Fact]
    public async Task Generic_void_does_not_echo_the_request_body()
    {
        await using var context = new RouteContext();
        var exchange = Generic("DELETE", "modules/42");
        exchange.In.Body = "request-payload";
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        await new ControllerDispatcherProcessor(registry, context).Process(exchange);

        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task Http_void_does_not_echo_the_request_body()
    {
        await using var context = new RouteContext();
        var exchange = Http("DELETE", "/modules/42");
        exchange.In.Body = "request-payload"u8.ToArray();
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));

        await new HttpControllerDispatcher(registry, context).Process(exchange);

        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task SignalR_void_does_not_echo_the_request_arguments()
    {
        await using var context = new RouteContext();
        var exchange = ByName("redbSignalR.Method", nameof(EchoController.Delete));
        exchange.In.Body = 42;

        await new SignalRControllerDispatcher(context, typeof(EchoController)).Process(exchange);

        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task Grpc_void_does_not_echo_the_request_payload()
    {
        await using var context = new RouteContext();
        var exchange = ByName(GrpcControllerDispatcher.MethodHeader, nameof(EchoController.Delete));
        exchange.In.Body = "42"u8.ToArray();

        await new GrpcControllerDispatcher(context, typeof(EchoController)).Process(exchange);

        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    // -- An action that wrote Out itself and returned null: its reply is the reply ---------------------------

    private static void ShouldBeTheActionsOwnReply(IExchange exchange)
    {
        exchange.Out!.GetHeader<int>("status.code").Should().Be(404);
        exchange.Out.GetHeader<string>("Content-Type").Should().Be("application/json");
        exchange.Out.Body.Should().BeOfType<ControllerErrorResponse>().Which.Message.Should().Be("no such thing");
    }

    [Fact]
    public async Task Generic_keeps_the_reply_the_action_wrote()
    {
        await using var context = new RouteContext();
        var exchange = Generic("GET", "self-reply");
        exchange.In.Body = "request-payload";
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(SelfReplyController));

        await new ControllerDispatcherProcessor(registry, context).Process(exchange);

        ShouldBeTheActionsOwnReply(exchange);
    }

    [Fact]
    public async Task Http_keeps_the_reply_the_action_wrote()
    {
        await using var context = new RouteContext();
        var exchange = Http("GET", "/self-reply");
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(SelfReplyController));

        await new HttpControllerDispatcher(registry, context).Process(exchange);

        ShouldBeTheActionsOwnReply(exchange);
        exchange.Out!.GetHeader<int>(HttpControllerDispatcher.HttpResponseCodeHeader).Should().Be(404);
    }

    [Fact]
    public async Task SignalR_keeps_the_reply_the_action_wrote()
    {
        await using var context = new RouteContext();
        var exchange = ByName("redbSignalR.Method", nameof(SelfReplyController.Missing));

        await new SignalRControllerDispatcher(context, typeof(SelfReplyController)).Process(exchange);

        ShouldBeTheActionsOwnReply(exchange);
    }

    [Fact]
    public async Task Grpc_keeps_the_reply_the_action_wrote()
    {
        await using var context = new RouteContext();
        var exchange = ByName(GrpcControllerDispatcher.MethodHeader, nameof(SelfReplyController.Missing));

        await new GrpcControllerDispatcher(context, typeof(SelfReplyController)).Process(exchange);

        ShouldBeTheActionsOwnReply(exchange);
    }

    // ── ValueTask<T>: the result, not the struct ─────────

    [Fact]
    public async Task Generic_ValueTask_of_T_returns_its_result()
    {
        await using var context = new RouteContext();
        var exchange = Generic("GET", "shapes/value");

        await new ControllerDispatcherProcessor(Registry(), context).Process(exchange);

        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
        exchange.Out.Body.Should().Be("value-ok");
    }

    [Fact]
    public async Task Http_ValueTask_of_T_returns_its_result()
    {
        await using var context = new RouteContext();
        var exchange = Http("GET", "/shapes/value");

        await new HttpControllerDispatcher(Registry(), context).Process(exchange);

        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
        exchange.Out.Body.Should().Be("value-ok");
    }

    [Fact]
    public async Task SignalR_ValueTask_of_T_returns_its_result()
    {
        await using var context = new RouteContext();
        var exchange = ByName("redbSignalR.Method", nameof(ReturnShapesController.Value));

        await new SignalRControllerDispatcher(context, typeof(ReturnShapesController)).Process(exchange);

        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
        exchange.Out.Body.Should().Be("value-ok");
    }

    [Fact]
    public async Task Grpc_ValueTask_of_T_returns_its_result()
    {
        await using var context = new RouteContext();
        var exchange = ByName(GrpcControllerDispatcher.MethodHeader, nameof(ReturnShapesController.Value));

        await new GrpcControllerDispatcher(context, typeof(ReturnShapesController)).Process(exchange);

        exchange.Out!.GetHeader<int>("status.code").Should().Be(200);
        exchange.Out.Body.Should().Be("value-ok");
    }

    // ── async ValueTask: awaited, then no result ─────────

    [Fact]
    public async Task Generic_async_ValueTask_is_awaited_and_is_204()
    {
        await using var context = new RouteContext();
        var exchange = Generic("POST", "shapes/value-done");

        await new ControllerDispatcherProcessor(Registry(), context).Process(exchange);

        exchange.getProperty("done").Should().Be(true);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task Http_async_ValueTask_is_awaited_and_is_204()
    {
        await using var context = new RouteContext();
        var exchange = Http("POST", "/shapes/value-done");

        await new HttpControllerDispatcher(Registry(), context).Process(exchange);

        exchange.getProperty("done").Should().Be(true);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task SignalR_async_ValueTask_is_awaited_and_is_204()
    {
        await using var context = new RouteContext();
        var exchange = ByName("redbSignalR.Method", nameof(ReturnShapesController.ValueDone));

        await new SignalRControllerDispatcher(context, typeof(ReturnShapesController)).Process(exchange);

        exchange.getProperty("done").Should().Be(true);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }

    [Fact]
    public async Task Grpc_async_ValueTask_is_awaited_and_is_204()
    {
        await using var context = new RouteContext();
        var exchange = ByName(GrpcControllerDispatcher.MethodHeader, nameof(ReturnShapesController.ValueDone));

        await new GrpcControllerDispatcher(context, typeof(ReturnShapesController)).Process(exchange);

        exchange.getProperty("done").Should().Be(true);
        exchange.Out!.GetHeader<int>("status.code").Should().Be(204);
        exchange.Out.Body.Should().BeNull();
    }
}
