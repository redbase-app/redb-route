using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Xml.CodeGen;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The catalog command creates each component with a constructor callable without arguments, and names on stderr
/// every one it cannot create, so a scheme never drops out of the catalog in silence.
/// </summary>
public sealed class CatalogComponentConstructionTests
{
    /// <summary>A component whose only constructor needs an argument, as mcp's did.</summary>
    public sealed class NeedsArgumentComponent(string required) : ComponentBase
    {
        public override string Scheme => "needs-" + required;

        public override IEndpoint CreateEndpoint(EndpointUri uri) => throw new NotSupportedException();
    }

    /// <summary>A component whose constructor throws.</summary>
    public sealed class ThrowingComponent : ComponentBase
    {
        public ThrowingComponent() => throw new InvalidOperationException("no config");

        public override string Scheme => "throwing";

        public override IEndpoint CreateEndpoint(EndpointUri uri) => throw new NotSupportedException();
    }

    [Fact]
    public void A_component_without_an_argument_free_constructor_is_named_on_one_warning_line()
    {
        var warnings = new StringWriter();

        var components = ComponentConstruction
            .Create([typeof(NeedsArgumentComponent), typeof(TimerComponent), typeof(LogComponent)], warnings)
            .ToList();

        components.Select(c => c.Scheme).Should().BeEquivalentTo(["timer", "log"], "log's constructor has only optional parameters");
        warnings.ToString().Should().Be(
            $"warning: {typeof(NeedsArgumentComponent).FullName}: no constructor callable without arguments; " +
            "its scheme is not in the catalog." + Environment.NewLine);
    }

    [Fact]
    public void A_constructor_that_throws_is_named_with_its_reason()
    {
        var warnings = new StringWriter();

        ComponentConstruction.Create([typeof(ThrowingComponent)], warnings).Should().BeEmpty();

        warnings.ToString().Should().Be(
            $"warning: {typeof(ThrowingComponent).FullName}: constructor failed (no config); its scheme is not in the catalog."
            + Environment.NewLine);
    }

    [Fact]
    public void Abstract_types_and_non_components_are_not_reported()
    {
        var warnings = new StringWriter();

        ComponentConstruction.Create([typeof(ComponentBase), typeof(string)], warnings).Should().BeEmpty();

        warnings.ToString().Should().BeEmpty();
    }
}
