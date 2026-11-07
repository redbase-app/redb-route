using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>A connector whose options name registry objects in every way an option can.</summary>
public sealed class ProbeRefComponent : ComponentBase
{
    public override string Scheme => "proberef";

    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException("catalog-only test component");
}

/// <summary>The options of <see cref="ProbeRefComponent"/>.</summary>
public sealed class ProbeRefEndpointOptions : EndpointOptions
{
    [ConnectionFactoryReference(typeof(ProbeRefFactory))]
    public string? ConnectionFactory { get; set; }

    [RegistryReference(typeof(IHeaderFilterStrategy))]
    public string? HeaderFilterStrategy { get; set; }

    [RegistryReference]
    public string? Anything { get; set; }

    public string? Queue { get; set; }

    public override void Validate()
    {
    }
}

/// <summary>The factory type <see cref="ProbeRefEndpointOptions.ConnectionFactory"/> expects.</summary>
public sealed class ProbeRefFactory;

/// <summary>
/// Row 7: an option that names a registry object carries <see cref="CatalogOption.Reference"/>, the expected type as
/// <c>Full.Name, Assembly</c> or <c>object</c>; every other option has none, and the JSON writes the member only when it
/// is set. The option's <c>type</c> stays what it was.
/// </summary>
public sealed class CatalogReferenceTests
{
    private static readonly IReadOnlyList<CatalogOption> Options = ComponentCatalog.Build([new ProbeRefComponent()]).Single().Options;

    private static CatalogOption Option(string name) => Options.Single(o => o.Name == name);

    private static string T<TType>() => $"{typeof(TType).FullName}, {typeof(TType).Assembly.GetName().Name}";

    [Fact]
    public void ATypedMark_IsTheExpectedType()
    {
        Option("HeaderFilterStrategy").Reference.Should().Be(T<IHeaderFilterStrategy>());
        Option("HeaderFilterStrategy").Type.Should().Be("string");
    }

    [Fact]
    public void AnUntypedMark_IsObject()
    {
        Option("Anything").Reference.Should().Be("object");
    }

    [Fact]
    public void TheConnectionFactoryReference_IsARegistryReferenceToo_AndKeepsItsFlag()
    {
        Option("ConnectionFactory").Reference.Should().Be(T<ProbeRefFactory>());
        Option("ConnectionFactory").ConnectionFactoryReference.Should().BeTrue();
    }

    [Fact]
    public void AnUnmarkedOption_HasNoReference()
    {
        Option("Queue").Reference.Should().BeNull();
    }

    [Fact]
    public void TheJson_WritesReferenceOnlyWhereItIsSet()
    {
        var json = ComponentCatalog.ToJson(ComponentCatalog.Build([new ProbeRefComponent()]));

        json.Should().Contain($"\"reference\": \"{T<IHeaderFilterStrategy>()}\"").And.Contain("\"reference\": \"object\"");
        System.Text.RegularExpressions.Regex.Matches(json, "\"reference\"").Count.Should().Be(3);
    }

    [Fact]
    public void TheCoreVmEndpoint_MarksItsHeaderFilterStrategy()
    {
        var vm = ComponentCatalog.Build([new VmComponent()]).Single();

        vm.Options.Single(o => o.Name == "HeaderFilterStrategy").Reference.Should().Be(T<IHeaderFilterStrategy>());
        vm.Options.Where(o => o.Name != "HeaderFilterStrategy").Should().OnlyContain(o => o.Reference == null);
    }
}
