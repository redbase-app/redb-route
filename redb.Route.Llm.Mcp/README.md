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

## Out of scope (Wave 2+)

- MCP `resources` and `prompts`.
- MCP `sampling` (server asking the host to do an LLM call).
- Allow-list / sandboxing of MCP servers.
