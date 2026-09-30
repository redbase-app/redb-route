using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// <see cref="IControllerActionFilter"/> is an onion: <c>BeforeAsync</c> ascending by <c>Order</c>,
/// <c>AfterAsync</c> descending, one <see cref="ControllerActionContext"/> shared by all of them, with the result,
/// status and elapsed time filled in before the first <c>AfterAsync</c>.
/// </summary>
public class ActionFilterOrderTests
{
    private sealed class RecordingFilter(int order, List<string> calls) : IControllerActionFilter
    {
        public int Order => order;

        public Task BeforeAsync(ControllerActionContext context, CancellationToken ct)
        {
            calls.Add($"before:{order}");
            context.Items[$"seen-by-{order}"] = true;
            return Task.CompletedTask;
        }

        public Task AfterAsync(ControllerActionContext context, CancellationToken ct)
        {
            calls.Add($"after:{order}:{context.StatusCode}:{context.Result}:{context.Items.Count}:{context.Elapsed >= TimeSpan.Zero}");
            return Task.CompletedTask;
        }
    }

    private static IExchange Get(string path)
    {
        var exchange = new Exchange();
        exchange.In.setHeader(ControllerDispatcherProcessor.MethodHeader, "GET");
        exchange.In.setHeader(ControllerDispatcherProcessor.PathHeader, path);
        return exchange;
    }

    [Fact]
    public async Task Before_runs_ascending_and_After_descending()
    {
        var calls = new List<string>();
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(ModulesController));
        await using var context = new RouteContext();

        // Passed out of order: the dispatcher sorts them.
        var dispatcher = new ControllerDispatcherProcessor(registry, context,
            [new RecordingFilter(2, calls), new RecordingFilter(1, calls), new RecordingFilter(3, calls)]);

        await dispatcher.Process(Get("modules/5"));

        calls.Should().Equal(
            "before:1", "before:2", "before:3",
            "after:3:200:module-5:3:True", "after:2:200:module-5:3:True", "after:1:200:module-5:3:True");
    }

    [Fact]
    public async Task After_sees_the_error_status_and_the_exception_of_a_failed_action()
    {
        var calls = new List<string>();
        Exception? seen = null;
        var registry = new ControllerRegistry();
        registry.RegisterController(typeof(FilterProbeController));
        await using var context = new RouteContext();

        var dispatcher = new ControllerDispatcherProcessor(registry, context,
            [new RecordingFilter(1, calls), new ExceptionProbe(e => seen = e)]);

        await dispatcher.Process(Get("filter-probe/fail"));

        calls.Should().Equal("before:1", "after:1:500::2:True");
        seen.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("probe-failed");
    }

    private sealed class ExceptionProbe(Action<Exception?> onAfter) : IControllerActionFilter
    {
        public int Order => 0;
        public Task BeforeAsync(ControllerActionContext context, CancellationToken ct)
        {
            context.Items["probe"] = true;
            return Task.CompletedTask;
        }
        public Task AfterAsync(ControllerActionContext context, CancellationToken ct)
        {
            onAfter(context.Exception);
            return Task.CompletedTask;
        }
    }
}
