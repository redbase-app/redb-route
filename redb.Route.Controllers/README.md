# redb.Route.Controllers

Transport-agnostic controller dispatch for the [redb.Route](../../README.md) ESB framework.  
Provides a `RedbController` base class, attribute-based routing, parameter binding, and four dispatcher implementations for **generic**, **HTTP**, **SignalR**, and **gRPC** transports with 13 DSL extension methods. Controllers are transport-unaware — the same controller class works behind any InOut endpoint.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Controllers?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Controllers)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Quick Start

```csharp
// Define a controller
[Route("orders")]
public class OrdersController : RedbController
{
    [HttpGet("{id}")]
    public Task<Order> GetOrder(int id)
    {
        return Task.FromResult(new Order { Id = id, Title = "Sample" });
    }

    [HttpPost]
    public Task CreateOrder([FromBody] Order order)
    {
        // Access route context and exchange
        var ctx = Context;
        var exchange = Exchange;
        return Task.CompletedTask;
    }
}

// Register and dispatch (generic)
var registry = new ControllerRegistry();
registry.RegisterAssembly(typeof(OrdersController).Assembly);

route.From("direct://api")
     .RedbController(registry);

// Or single controller shorthand
route.From("direct://api")
     .RedbController<OrdersController>();
```

## Architecture

This is **not** a connector (no `IComponent`, no URI scheme, no producer/consumer). It is a processing library that provides `IProcessor` implementations and DSL extension methods. Controllers sit behind `direct:` or transport endpoints and dispatch based on headers set by the upstream consumer.

```
HTTP Consumer → [redbHttp.Method, redbHttp.Path] → HttpControllerDispatcher → OrdersController.GetOrder()
SignalR Hub   → [redbSignalR.Method]              → SignalRControllerDispatcher → ChatController.Send()
gRPC Service  → [dispatch-method]                 → GrpcControllerDispatcher → CalcController.Add()
Any Endpoint  → [route.method, route.path]        → ControllerDispatcherProcessor → generic dispatch
```

## RedbController Base Class

All controllers inherit from `RedbController`, which exposes:

| Property | Type | Description |
|----------|------|-------------|
| `Context` | `IRouteContext` | Route context for the current invocation |
| `Exchange` | `IExchange` | Current exchange being processed |

Controllers are instantiated per-request via `Activator.CreateInstance()`. No DI constructor injection — use `Context` to access services.

## Attributes

### Route Attribute

Applied to the controller class. Sets the base path for all actions:

```csharp
[Route("modules")]           // explicit path
public class ModulesController : RedbController { }

// If omitted, defaults to class name minus "Controller" suffix, lowercased:
public class UsersController : RedbController { }  // base path: "users"
```

### HTTP Method Attributes

Applied to public methods. Define the HTTP method and optional sub-template:

| Attribute | Method | Example |
|-----------|--------|---------|
| `[HttpGet]` | GET | `[HttpGet]`, `[HttpGet("{id}")]` |
| `[HttpPost]` | POST | `[HttpPost]`, `[HttpPost("batch")]` |
| `[HttpPut]` | PUT | `[HttpPut("{id}")]` |
| `[HttpDelete]` | DELETE | `[HttpDelete("{id}")]` |
| `[HttpPatch]` | PATCH | `[HttpPatch("{id}")]` |

Templates support `{param}` placeholders that are extracted from the request path and matched to method parameters by name.

### Binding Attributes

| Attribute | Source | Example |
|-----------|--------|---------|
| `[FromBody]` | `exchange.In.Body` | `[FromBody] Order order` |
| `[FromHeader("X-Tenant")]` | `exchange.In.Headers["X-Tenant"]` | `[FromHeader("X-Tenant")] string tenant` |
| `[FromQuery("page")]` | Query parameter | `[FromQuery("page")] int page` |
| `[FromRoute("id")]` | Route template `{id}` | `[FromRoute("id")] int id` |
| `[FromProperty("user")]` | `exchange.Properties["user"]` | `[FromProperty("user")] User user` |

**Implicit binding** (no attribute):
- `CancellationToken` → `CancellationToken.None`
- Parameter name matches a route template `{param}` → bound from route
- Complex type → deserialized from body
- Simple type with default value → uses default

## Dispatchers

### ControllerDispatcherProcessor (Generic)

Reads `route.path` and `route.method` headers. Works with any transport.

```csharp
// Set headers before dispatch
exchange.In.Headers["route.path"] = "orders/42";
exchange.In.Headers["route.method"] = "GET";

// DSL
route.From("direct://api").RedbController(registry);
route.From("direct://api").RedbController<OrdersController>();
route.From("direct://api").RedbController<OrdersController>("GetOrder"); // direct method call
```

### HttpControllerDispatcher

Reads `redbHttp.Method` and `redbHttp.Path` headers (set automatically by the HTTP consumer). Also merges `redbHttp.RouteParam.*` and reads `redbHttp.QueryParam.*` for parameter binding.

```csharp
route.From("http://0.0.0.0:8080/api")
     .RedbHttpController(registry);

route.From("http://0.0.0.0:8080/api")
     .RedbHttpController<OrdersController>();
```

### SignalRControllerDispatcher

Reads `redbSignalR.Method` header. Parameters are resolved positionally from the exchange body (as sent by the SignalR client).

```csharp
route.From("signalr://bridge")
     .RedbSignalRController<ChatController>();

// Multiple controllers
route.From("signalr://bridge")
     .RedbSignalRController(typeof(ChatController), typeof(NotificationController));

// Method resolution: "Send" or "Chat.Send" (qualified)
```

### GrpcControllerDispatcher

Reads `dispatch-method` header from gRPC metadata. Body is treated as JSON — single value or array of positional arguments.

```csharp
route.From("grpc://0.0.0.0:5000")
     .RedbGrpcController<CalcController>();

// Multiple controllers
route.From("grpc://0.0.0.0:5000")
     .RedbGrpcController(typeof(CalcController), typeof(DataController));
```

## ControllerRegistry

Builds a route lookup table by scanning assemblies or registering individual controller types:

```csharp
var registry = new ControllerRegistry();

// Scan entire assembly
int count = registry.RegisterAssembly(typeof(OrdersController).Assembly);

// Or register individual controllers
registry.RegisterController(typeof(OrdersController));
registry.RegisterController(typeof(UsersController));

// Resolve manually
var action = registry.Resolve(HttpMethodType.Get, "orders/42", out var routeParams);
// action.ControllerType = typeof(OrdersController)
// action.Method = GetOrder
// routeParams["id"] = "42"
```

## Response Conventions

| Scenario | `status.code` | Body |
|----------|---------------|------|
| Method returns a value | `200` | Return value (JSON-serialized for HTTP/gRPC) |
| Method returns `null` or `Task` (void) | `204` | — |
| Missing headers | `400` | `ControllerErrorResponse` |
| No matching action | `404` | `ControllerErrorResponse` |
| Exception during invocation | `500` | `ControllerErrorResponse` |

```csharp
// Error response model
public sealed class ControllerErrorResponse
{
    public string Error { get; init; }      // e.g. "NotFound", "BadRequest", "InternalError"
    public string Message { get; init; }    // Human-readable description
    public int StatusCode { get; init; }    // HTTP status code
}
```

## DSL Extension Methods

All methods are on `IRouteDefinition`:

| Method | Dispatcher | Headers Read |
|--------|-----------|--------------|
| `RedbController(registry)` | Generic | `route.path`, `route.method` |
| `RedbController<T>()` | Generic | `route.path`, `route.method` |
| `RedbController<T>(methodName)` | Direct invoke | — |
| `RedbController(registry, ctrlName, methodName)` | Direct invoke | — |
| `RedbController(registry, ctrlExpr, methodExpr)` | Dynamic | Exchange-dependent |
| `RedbHttpController(registry)` | HTTP | `redbHttp.Method`, `redbHttp.Path` |
| `RedbHttpController<T>()` | HTTP | `redbHttp.Method`, `redbHttp.Path` |
| `RedbSignalRController<T>()` | SignalR | `redbSignalR.Method` |
| `RedbSignalRController(types)` | SignalR | `redbSignalR.Method` |
| `RedbSignalRController(registry)` | SignalR | `redbSignalR.Method` |
| `RedbGrpcController<T>()` | gRPC | `dispatch-method` |
| `RedbGrpcController(types)` | gRPC | `dispatch-method` |
| `RedbGrpcController(registry)` | gRPC | `dispatch-method` |

## Requirements

- .NET 8.0 / 9.0 / 10.0
- `redb.Route` (core) — no external dependencies
