using System.Collections.Concurrent;
using redb.Route.Configuration;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Descriptor for a per-tenant tool: its address is built from the caller's tenant header, which is
/// the one thing <see cref="ILlmToolDescriptor.BuildEndpointUri"/> may legitimately do with the
/// parent exchange. Exists to prove the tool cache keys on the resolved address — two tenants with
/// the same input must never share an entry.
/// </summary>
public sealed class TenantScopedToolDescriptor : ILlmToolDescriptor
{
    /// <summary>Header carrying the tenant id (<see cref="ToolHeaderPolicy"/> would propagate this in a real host).</summary>
    public const string TenantHeader = "x-tenant-id";

    /// <summary>Tool name exposed to the model.</summary>
    public const string ToolName = "lookup";

    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";

    /// <inheritdoc />
    public LlmToolCapability Capability { get; } = new()
    {
        Name = ToolName,
        Description = "Look up a fact for the calling tenant.",
        InputSchema = Schema,
        Safety = new LlmToolSafety
        {
            SideEffect = ToolSideEffect.ReadOnly,
            Caching = ToolCachingPolicy.Persist
        }
    };

    /// <inheritdoc />
    public string BuildEndpointUri(string inputJson, IExchange parentExchange)
        => $"direct:tool-lookup.{ResolveTenant(parentExchange)}";

    /// <summary>Tenant id of <paramref name="exchange"/>, or <c>none</c> when the header is absent.</summary>
    public static string ResolveTenant(IExchange exchange)
        => exchange.In.Headers.TryGetValue(TenantHeader, out var tenant) && tenant is not null
            ? tenant.ToString()!
            : "none";
}

/// <summary>
/// Mounts one <c>direct:tool-lookup.{tenant}</c> endpoint per tenant and counts dispatches, so a test
/// can assert both "the right tenant was called" and "the other tenant's cached answer was not
/// served". Each endpoint answers with its own tenant id and its own call counter — a cross-tenant
/// leak would be visible in the payload, not only in the counters.
/// </summary>
public sealed class TenantScopedToolRoutes : RouteBuilder
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";

    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Dispatches per tenant id.</summary>
    public IReadOnlyDictionary<string, int> Counts => _counts;

    /// <summary>Dispatches for one tenant id (0 when never called).</summary>
    public int CountFor(string tenant) => _counts.TryGetValue(tenant, out var n) ? n : 0;

    /// <inheritdoc />
    protected override void Configure()
    {
        Mount("a");
        Mount("b");
        Mount("none");
    }

    private void Mount(string tenant)
    {
        From($"direct:tool-lookup.{tenant}")
            .Process(e =>
            {
                var call = _counts.AddOrUpdate(tenant, 1, (_, n) => n + 1);
                e.Out ??= e.In.Clone();
                e.Out.Body = $$"""{"tenant":"{{tenant}}","call":{{call}}}""";
                e.Out.Headers["Content-Type"] = "application/json";
            });
    }
}
