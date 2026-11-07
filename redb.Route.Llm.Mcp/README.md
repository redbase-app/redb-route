# redb.Route.Llm.Mcp

MCP (Model Context Protocol) client connector for **redb.Route.Llm**. Spawns external MCP servers (stdio + HTTP+SSE), discovers their tool catalogue via `tools/list`, and projects every remote tool into the existing `IToolDescriptorRegistry` so the agent picks them up like any native redb tool.

## Wave 1 (current)

- `mcp://` scheme — `mcp://serverName/toolName` invokes `tools/call`.
- Stdio transport (spawn external process; newline-delimited JSON-RPC).
- HTTP + SSE transport (POST request, SSE for server-initiated frames).
- `IHostedService` discovery on host startup — `initialize` + `tools/list`.
- Listens for `notifications/tools/list_changed` and refreshes descriptors.
- Cancellation flows through `IProducerTemplate.RequestBody(uri, msg, ct)` → `IProducer.Process(exchange, ct)` → `IMcpClient.CallToolAsync(..., ct)` → `notifications/cancelled`.
- Tool-name sanitisation: `{server}__{tool}`, server ≤ 24 chars, tool ≤ 36, total ≤ 64.
- Safety defaults to `External` + `Cheap` + no approval; per-tool override via regex in `McpServerOptions.SafetyOverrides`.

## Quickstart

```csharp
services.AddRedbRoute(...);
services.AddRedbRouteLlm(...);
services.AddRedbRouteMcp();

services.AddMcpServer("serena", McpTransport.Stdio(
    "uvx",
    [
        "--from", "git+https://github.com/oraios/serena",
        "serena", "start-mcp-server",
        "--context", "ide",
        "--project", "C:/path/to/project",
    ]));
```

The agent now sees Serena's full tool catalogue (`serena__find_symbol`, `serena__get_symbols_overview`, …) alongside any native redb tools.

## In a Tsak module

A module gets the context in `InitRoute.main` and builds the registry itself, from settings it reads from its
configuration; the connector does not read configuration. Give the registry to the context, then call `mcp://` from C#
routes or from the package's XML routes:

```csharp
public static class InitRoute
{
    public static async Task<IRouteContext> main(IRouteContext ctx)
    {
        // command, arguments and logger: the module's own, from its configuration and logging
        var registry = new McpRegistry();
        var client = new StdioMcpClient("serena", McpTransport.Stdio(command, arguments), logger);
        await client.InitializeAsync();
        registry.Register(client);
        ctx.AddService(typeof(IMcpRegistry), registry);   // McpComponent() finds it here
        return ctx;
    }
}
```

```xml
<!-- context.xml of the package: the component, created without arguments, takes the registry above -->
<components>
  <component type="redb.Route.Llm.Mcp.McpComponent, redb.Route.Llm.Mcp"/>
</components>

<!-- a route: a target only, mcp has no consumer side -->
<to><mcp path="serena/find_symbol" callTimeoutMs="30000"/></to>
```

`new McpComponent()` takes the registry the context holds: the one module code added with `ctx.AddService`, else the
`IMcpRegistry` singleton of `AddRedbRouteMcp()` in a hosted app. Without either, creating an `mcp://` endpoint fails
and the message names both ways. `new McpComponent(registry)` and `AddRedbRouteMcp()` work as before.

## Tracing

On the `redb.Route` activity source (`AddSource("redb.Route")`): one `Client` span per `tools/call`,
`mcp.tools/call {server}/{tool}`, with `rpc.system` = `mcp`, `rpc.service`, `rpc.method` and `redb.route.endpoint`. An
error when the call fails (an unknown or dead server, a tool error, the call timeout), unless our own token cancelled
it. `RouteEngineOptions.EnableTelemetry = false` opens none.

## Out of scope (Wave 2+)

- MCP `resources` and `prompts`.
- MCP `sampling` (server asking the host to do an LLM call).
- Allow-list / sandboxing of MCP servers.
