using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Tests.Llm.Mcp;

/// <summary>
/// Spawns a single Serena MCP server once per xUnit collection and exposes the
/// connected client + tool catalogue. Tests that don't need their own private
/// process reuse this fixture to keep startup cost (uvx download / venv) at one
/// hit per run.
/// </summary>
public sealed class SerenaFixture : IAsyncLifetime
{
    /// <summary>Live MCP client connected to Serena. Null when fixture skipped initialization.</summary>
    public IMcpClient? Client { get; private set; }

    /// <summary>Tool catalogue returned by the initial <c>tools/list</c>.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; private set; } = [];

    /// <summary>Resolved launch command (for diagnostic display + RestartTests).</summary>
    internal SerenaLaunch? Launch { get; private set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var launch = SerenaConfig.Launch;
        if (launch is null) return;
        Launch = launch;

        var transport = McpTransport.Stdio(launch.Command, launch.Arguments);
        var client = new redb.Route.Llm.Mcp.Transport.StdioMcpClient("serena", transport, NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await client.InitializeAsync(cts.Token).ConfigureAwait(false);
            Tools = await client.ListToolsAsync(cts.Token).ConfigureAwait(false);
            Client = client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (Client is not null)
            await Client.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Collection definition that shares a single <see cref="SerenaFixture"/> across tests.</summary>
[CollectionDefinition("SerenaSerial", DisableParallelization = true)]
public sealed class SerenaCollection : ICollectionFixture<SerenaFixture> { }
