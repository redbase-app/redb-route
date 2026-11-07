using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

/// <summary>Direct-invoke target of <c>RedbController&lt;T&gt;(methodName)</c>: no HTTP attributes, reached by name.</summary>
public class DirectInvokeController : RedbController
{
    public async Task<string> Greet([FromBody] string name)
    {
        await Task.Yield();
        return $"hello {name}";
    }

    // async Task with no result: the Task an async method returns is a Task<VoidTaskResult> at run time.
    public async Task Touch()
    {
        await Task.Yield();
        Exchange.setProperty("touched", true);
    }

    public string Boom() => throw new InvalidOperationException("direct-boom");
}

/// <summary>
/// The asynchronous return shapes, each reachable by path (generic, HTTP) and by name (SignalR, gRPC, SOAP).
/// Every method yields first, so a dispatcher that does not await it finds the side effect missing.
/// </summary>
[Route("shapes")]
public class ReturnShapesController : RedbController
{
    [HttpPost("done")]
    public async Task Done()
    {
        await Task.Yield();
        Exchange.setProperty("done", true);
    }

    [HttpGet("value")]
    public async ValueTask<string> Value()
    {
        await Task.Yield();
        return "value-ok";
    }

    [HttpPost("value-done")]
    public async ValueTask ValueDone()
    {
        await Task.Yield();
        Exchange.setProperty("done", true);
    }
}

/// <summary>
/// A SOAP operation that is also an HTTP action, so a <see cref="ControllerRegistry"/> (which registers attributed
/// actions only) can carry it into <c>RedbSoapController(registry)</c>.
/// </summary>
public class SoapFareController : RedbController
{
    [HttpPost]
    public SoapControllerDispatcherTests.GetFaresResponse GetFares([FromBody] SoapControllerDispatcherTests.GetFares request)
        => new() { Price = 7 };
}

public enum BindingPriority { Low, High }

public sealed record BoundTypes(DateTime At, DateTimeOffset Offset, decimal Amount, BindingPriority Priority, Guid Id);

public class OptionsRequest
{
    public string? ModuleName { get; set; }
}

/// <summary>One action per binding source, each returning what it bound so a test reads values, not a status.</summary>
[Route("binding")]
public class BindingController : RedbController
{
    [HttpGet("header")]
    public string Header([FromHeader("X-Tenant")] string tenant, [FromHeader("X-Limit")] int limit = 10)
        => $"{tenant}:{limit}";

    [HttpGet("property")]
    public string Property([FromProperty("user")] string user, [FromProperty("attempt")] int attempt)
        => $"{user}:{attempt}";

    [HttpGet("query")]
    public string Query([FromQuery("verbose")] bool verbose, [FromQuery("page")] int page = 1)
        => $"{verbose}:{page}";

    [HttpGet("types")]
    public BoundTypes Types(
        [FromQuery("at")] DateTime at,
        [FromQuery("offset")] DateTimeOffset offset,
        [FromQuery("amount")] decimal amount,
        [FromQuery("priority")] BindingPriority priority,
        [FromQuery("id")] Guid id)
        => new(at, offset, amount, priority, id);

    [HttpPatch("{id}")]
    public string Patch([FromRoute("id")] int id, [FromBody] CreateModuleRequest request) => $"patched {id}:{request.Name}";

    [HttpPost("options")]
    public object Options([FromBody] OptionsRequest request) => new { request.ModuleName, IsCreated = true };
}

/// <summary>A literal segment and a placeholder at the same depth, the literal action declared first.</summary>
[Route("sessions-literal-first")]
public class SessionsLiteralFirstController : RedbController
{
    [HttpDelete("current")]
    public string DeleteCurrent() => "current";

    [HttpDelete("{sessionId}")]
    public string DeleteById([FromRoute("sessionId")] string sessionId) => $"by-id:{sessionId}";
}

/// <summary>The same pair declared the other way round, so the winner cannot depend on declaration order.</summary>
[Route("sessions-param-first")]
public class SessionsParamFirstController : RedbController
{
    [HttpDelete("{sessionId}")]
    public string DeleteById([FromRoute("sessionId")] string sessionId) => $"by-id:{sessionId}";

    [HttpDelete("current")]
    public string DeleteCurrent() => "current";
}

/// <summary>An action that fails, so filters can be checked on the error path.</summary>
[Route("filter-probe")]
public class FilterProbeController : RedbController
{
    [HttpGet("fail")]
    public string Fail() => throw new InvalidOperationException("probe-failed");
}

// -- Shared by the registry, generic, HTTP, SignalR and gRPC suites --------------------------------------

[Route("modules")]
public class ModulesController : RedbController
{
    [HttpGet]
    public string[] GetAll() => ["module1", "module2"];

    [HttpGet("{id}")]
    public string GetById([FromRoute("id")] string id) => $"module-{id}";

    [HttpPost]
    public object Create([FromBody] CreateModuleRequest request) =>
        new { Name = request.Name, Created = true };

    [HttpDelete("{id}")]
    public void Delete([FromRoute("id")] string id) { }

    [HttpPut("{id}")]
    public object Update([FromRoute("id")] string id, [FromBody] CreateModuleRequest request) =>
        new { Id = id, Name = request.Name, Updated = true };
}

[Route("contexts")]
public class ContextsController : RedbController
{
    [HttpGet]
    public string[] List() => ["ctx1", "ctx2"];

    [HttpPost("{name}/start")]
    public object Start([FromRoute("name")] string name) =>
        new { Name = name, Started = true };

    [HttpGet("{name}/status")]
    public object Status([FromRoute("name")] string name, [FromQuery("verbose")] bool verbose) =>
        new { Name = name, Verbose = verbose, Status = "running" };
}

public class NoRouteController : RedbController
{
    [HttpGet]
    public string Health() => "ok";
}

public class CreateModuleRequest
{
    public string Name { get; set; } = "";
}

/// <summary>
/// Method-name dispatch fixtures (SignalR, gRPC, positional binding). No [Route] and no HTTP attributes on purpose:
/// those transports dispatch by method name, so every public method is reachable, and a ControllerRegistry
/// (which registers attributed actions only) never sees them.
/// </summary>
public class EchoController : RedbController
{
    public string Echo(string message) => $"echo:{message}";

    public string[] GetAll() => ["item1", "item2"];

    public object GetById(int id) => new { Id = id, Name = $"item-{id}" };

    public object Create(CreateModuleRequest request) =>
        new { Name = request.Name, Created = true };

    public object Update(int id, CreateModuleRequest request) =>
        new { Id = id, Name = request.Name, Updated = true };

    public void Delete(int id) { }

    public async Task<string> AsyncMethod(string input)
    {
        await Task.Yield();
        return $"async:{input}";
    }

    public string WithDefault(string value, int count = 5) => $"{value}:{count}";

    public string WithCancellation(string value, CancellationToken ct) => $"ok:{value}";
}

/// <summary>Second controller for multi-controller dispatch tests.</summary>
public class StatusController : RedbController
{
    public string GetAll() => "status-ok";

    public object Health() => new { Status = "healthy", Uptime = 12345 };
}

/// <summary>Controller that verifies Context and Exchange are injected.</summary>
public class ContextCheckController : RedbController
{
    public string Check()
    {
        if (Context is null) throw new InvalidOperationException("Context is null");
        if (Exchange is null) throw new InvalidOperationException("Exchange is null");
        return "ok";
    }
}

/// <summary>Controller exercising the JSON-object name-binding rules for <c>ResolvePositional</c>.</summary>
public class BindController : RedbController
{
    public string CreateBody([FromBody] CreateModuleRequest r) => r.Name;          // [FromBody] → whole object
    public string CreateBare(CreateModuleRequest request) => request.Name;         // lone complex → whole object
    public string Update([FromRoute("id")] string id, [FromBody] CreateModuleRequest r) => $"{id}|{r.Name}";
    public string List(int offset = 0, int count = 25) => $"{offset}:{count}";     // simple params by key, defaults kept
}

/// <summary>Reports which instance served the call, so a test can tell one instance per request from a shared one.</summary>
[Route("instance")]
public class InstanceProbeController : RedbController
{
    private readonly Guid _instanceId = Guid.NewGuid();

    [HttpGet]
    public string Which() => $"{_instanceId}|{Exchange.ExchangeId}";
}

/// <summary>A controller that asks for a constructor dependency, which the README says is not supported.</summary>
[Route("ctor-arg")]
public class ConstructorArgumentController(string dependency) : RedbController
{
    [HttpGet]
    public string Get() => dependency;
}

/// <summary>
/// An action that answers by itself, the way Tsak's ApiResponse helpers do: it writes Out (body, status,
/// Content-Type), stops the exchange and returns null. Reachable by path and by method name.
/// </summary>
[Route("self-reply")]
public class SelfReplyController : RedbController
{
    [HttpGet]
    public object? Missing()
    {
        Exchange.Out ??= Exchange.In.Clone();
        Exchange.Out.Body = new ControllerErrorResponse { Error = "NotFound", Message = "no such thing", StatusCode = 404 };
        Exchange.Out.Headers["status.code"] = 404;
        Exchange.Out.Headers["Content-Type"] = "application/json";
        Exchange.Stop();
        return null;
    }
}
