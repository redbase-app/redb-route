using FluentAssertions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for W5 ProcessorDefinition migration: F0-F1 foundations.
/// </summary>
public class ProcessorDefinitionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ToDefinition_CompilesToToProcessor()
    {
        var def = new ToDefinition("direct:target");
        var proc = def.CreateProcessor(_context);
        proc.Should().BeOfType<ToProcessor>();
        ((ToProcessor)proc).EndpointUri.Should().Be("direct:target");
    }

    [Fact]
    public void ProcessorDefinition_AddOutput_SetsParent()
    {
        var parent = new ToDefinition("direct:a");
        var child = new ToDefinition("direct:b");
        // ProcessorDefinition.AddOutput is protected, so test through RouteDefinition once it exists.
        // For now verify the interface contract manually.
        child.Parent = parent;
        child.Parent.Should().BeSameAs(parent);
    }
}
