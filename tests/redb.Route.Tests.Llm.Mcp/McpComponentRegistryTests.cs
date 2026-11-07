using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Llm.Mcp;

/// <summary>
/// <see cref="McpComponent"/> without a constructor argument takes the MCP client registry the context holds: the one
/// a Tsak module gives it in <c>InitRoute</c> (<c>ctx.AddService(typeof(IMcpRegistry), registry)</c>), or the host's DI
/// singleton of <c>AddRedbRouteMcp()</c>. So a package's markup can name the component and the catalog can describe it.
/// </summary>
public sealed class McpComponentRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-mcp-registry-" + Guid.NewGuid().ToString("N"));

    public McpComponentRegistryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>An MCP client that answers every tools/call with the tool's name, without a server.</summary>
    private sealed class EchoClient(string serverName) : IMcpClient
    {
        public string ServerName { get; } = serverName;
        public McpClientStatus Status => McpClientStatus.Healthy;
        public InitializeResult? Initialize => null;
        public event EventHandler? ToolsChanged { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolDefinition>>([]);
        public Task<CallToolResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(new CallToolResult { Content = new JsonArray(JsonValue.Create(toolName)) });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static McpRegistry Registry()
    {
        var registry = new McpRegistry();
        registry.Register(new EchoClient("srv"));
        return registry;
    }

    private static async Task<IExchange> Send(RouteContext ctx)
    {
        var template = new ProducerTemplate(ctx);
        template.Start();
        var exchange = new Exchange(new Message("{}"));
        await template.SendAsync("direct://in", exchange);
        return exchange;
    }

    [Fact]
    public async Task Takes_the_registry_a_module_gave_the_context()
    {
        await using var ctx = new RouteContext();
        ctx.AddService(typeof(IMcpRegistry), Registry());
        ctx.AddComponent(new McpComponent());
        ctx.AddRoutes(r => r.From("direct://in").To("mcp://srv/echo"));
        await ctx.Start();

        var exchange = await Send(ctx);

        exchange.Exception.Should().BeNull();
        exchange.In.Headers["Mcp-Tool"].Should().Be("echo");
    }

    [Fact]
    public async Task Takes_the_registry_of_the_host_services()
    {
        var services = new ServiceCollection().AddSingleton<IMcpRegistry>(Registry()).BuildServiceProvider();
        await using var ctx = new RouteContext(services);
        ctx.AddComponent(new McpComponent());

        var act = () => ctx.GetEndpoint("mcp://srv/echo");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Without_a_registry_creating_the_endpoint_names_both_ways()
    {
        await using var ctx = new RouteContext();
        ctx.AddComponent(new McpComponent());

        var act = () => ctx.GetEndpoint("mcp://srv/echo");

        act.Should().Throw<InvalidOperationException>().Which.Message.Should()
            .Contain("AddRedbRouteMcp").And.Contain("AddMcpServer")
            .And.Contain("InitRoute").And.Contain("ctx.AddService(typeof(IMcpRegistry), registry)");
    }

    [Fact]
    public async Task The_registry_given_to_the_constructor_still_serves()
    {
        await using var ctx = new RouteContext();
        ctx.AddComponent(new McpComponent(Registry()));
        ctx.AddRoutes(r => r.From("direct://in").To("mcp://srv/echo"));
        await ctx.Start();

        (await Send(ctx)).In.Headers["Mcp-Tool"].Should().Be("echo");
    }

    [Fact]
    public void AddRouteComponent_still_injects_the_registry_through_the_constructor()
    {
        var registry = Registry();
        var services = new ServiceCollection();
        services.AddSingleton<IMcpRegistry>(registry);
        services.AddRouteComponent<McpComponent>();

        services.BuildServiceProvider().GetRequiredService<McpComponent>().Registry.Should().BeSameAs(registry);
    }

    [Fact]
    public async Task A_package_names_the_component_in_markup_once_module_code_provided_the_registry()
    {
        await using var ctx = new RouteContext();
        ctx.AddService(typeof(IMcpRegistry), Registry());   // what InitRoute.main does in a Tsak module
        ctx.AddXmlContextFromContent($$"""
            <context xmlns="urn:redb:route:1.0">
              <components>
                <component type="{{typeof(McpComponent).FullName}}, {{typeof(McpComponent).Assembly.GetName().Name}}"/>
              </components>
            </context>
            """);
        ctx.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="mcp-xml">
                <from uri="direct://in"/>
                <to><mcp path="srv/echo" callTimeoutMs="5000"/></to>
              </route>
            </routes>
            """);
        await ctx.Start();

        (await Send(ctx)).In.Headers["Mcp-Tool"].Should().Be("echo");
    }

    private static readonly IReadOnlyList<CatalogComponent> Catalog = ComponentCatalog.Build([new McpComponent()]);

    [Fact]
    public void The_catalog_describes_mcp_as_producer_only_with_its_options()
    {
        var mcp = Catalog.Should().ContainSingle().Which;

        mcp.Scheme.Should().Be("mcp");
        mcp.Role.Should().Be(EndpointRole.Producer);
        mcp.Options.Select(o => o.Name).Should().BeEquivalentTo(["ServerName", "ToolName", "CallTimeoutMs"]);
    }

    [Fact]
    public void The_schema_takes_mcp_as_a_target_and_refuses_it_in_from()
    {
        IReadOnlyList<(int Line, int Column, string Message)> Validate(string from, string to)
            => XmlRouteSchema.Validate(XDocument.Parse($"""
                <routes xmlns="urn:redb:route:1.0">
                  <route id="r">
                    <from>{from}</from>
                    <to>{to}</to>
                  </route>
                </routes>
                """, LoadOptions.SetLineInfo), ElementRegistry.CreateDefault(), Catalog);

        Validate("""<mcp path="a/b"/>""", """<mcp path="a/b"/>""").Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Check_reports_mcp_in_from_at_its_position()
    {
        var dir = RouteProjectScaffold.Create(_root, "Mcp");
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="wrong">
                <from uri="mcp://a/b"/>
                <to uri="log://x"/>
              </route>
              <route id="right">
                <from uri="direct://in"/>
                <to><mcp path="a/b"/></to>
              </route>
            </routes>
            """);

        var errors = RoutePackage.Check(dir, "p", "1.0.0", catalog: Catalog).Errors.ToList();

        errors.Should().ContainSingle(e => e.Message.Contains("mcp")).Which.Should().Match<PackageFinding>(e =>
            e.File.StartsWith("routes/main.route.xml(3,")
            && e.Message.StartsWith("<from>: mcp has no consumer side: a route cannot start from it (mcp is producer-only)."));
    }
}
