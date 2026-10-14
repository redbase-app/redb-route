using System.Security.Authentication;
using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>An acknowledgement mode, spelled lowercase in the engine's own messages.</summary>
public enum ProbeAckMode
{
    Manual,
    Auto,
}

/// <summary>A connector with a pick-one enum option and a [Flags] one.</summary>
public sealed class ProbeTlsComponent : ComponentBase
{
    public override string Scheme => "probetls";

    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException("catalog-only test component");
}

/// <summary>A pick-one enum whose list is long enough to lose the case-insensitive union.</summary>
public enum ProbeFileExist
{
    Override,
    Append,
    Fail,
    Ignore,
}

/// <summary>The options <see cref="ProbeTlsComponent"/> binds.</summary>
public sealed class ProbeTlsEndpointOptions : EndpointOptions
{
    public ProbeAckMode AckMode { get; set; }

    public ProbeFileExist FileExist { get; set; }

    public SslProtocols SslProtocols { get; set; }

    public override void Validate()
    {
    }
}

/// <summary>Options two components share — the mail connector's shape (imap, pop3, smtp).</summary>
public sealed class ProbeSharedOptions : EndpointOptions
{
    public bool TrustAllCertificates { get; set; }

    public override void Validate()
    {
    }
}

/// <summary>A component whose options class is named after neither it nor its assembly's only one.</summary>
public sealed class ProbeInboxComponent : ComponentBase
{
    public override string Scheme => "probeinbox";

    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException("catalog-only test component");
}

/// <summary>The endpoint that declares the shared options as its type argument.</summary>
public sealed class ProbeInboxEndpoint(EndpointUri uri, IComponent component, ProbeSharedOptions options)
    : EndpointBase<ProbeSharedOptions>(uri, component, options)
{
    public override IProducer CreateProducer() => throw new NotSupportedException();

    public override IConsumer CreateConsumer(IProcessor processor) => throw new NotSupportedException();
}

/// <summary>
/// Enum options in the editor schema read the way the engine reads them: the option converter
/// parses member names in any case, and a [Flags] enum takes several members joined by commas.
/// The schema used to take exact C# names only (<c>ackMode="manual"</c>, the engine's own
/// spelling, was an error) and one flags member at most (<c>sslProtocols="Tls12,Tls13"</c> was one).
/// A LONG pick-one list is the one exception, and it is deliberate: the case-insensitive form costs
/// the editor's language server its memory, so above the threshold the schema lists the member names
/// and nothing more (see <c>XmlRouteSchema.EnumOptionType</c>).
/// </summary>
public sealed class CatalogEnumOptionsTests
{
    private static readonly IReadOnlyList<CatalogComponent> Catalog = ComponentCatalog.Build([new ProbeTlsComponent()]);

    private static IReadOnlyList<(int Line, int Column, string Message)> Validate(string attributes)
        => XmlRouteSchema.Validate(XDocument.Parse($"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="r">
                <from><probetls path="q" {attributes}/></from>
                <to uri="direct://out"/>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), ElementRegistry.CreateDefault(), Catalog);

    [Fact]
    public void Catalog_TellsAFlagsEnum_FromAPickOneEnum()
    {
        var options = Catalog.Single().Options;

        options.Single(o => o.Name == "AckMode").Type.Should().Be("enum");
        options.Single(o => o.Name == "SslProtocols").Type.Should().Be("flags");
    }

    [Fact]
    public void Catalog_FindsSharedOptions_ThroughTheEndpointsTypeArgument()
    {
        // imap, pop3 and smtp share MailEndpointOptions: neither naming nor "the assembly's only
        // options class" finds it, so their catalog entries had no options at all.
        var inbox = ComponentCatalog.Build([new ProbeInboxComponent()]).Single();

        inbox.OptionsType.Should().Be(typeof(ProbeSharedOptions).FullName);
        inbox.Options.Should().Contain(o => o.Name == "TrustAllCertificates");
    }

    [Theory]
    [InlineData("ackMode=\"manual\"")]
    [InlineData("ackMode=\"Manual\"")]
    [InlineData("ackMode=\"AUTO\"")]
    [InlineData("sslProtocols=\"Tls12,Tls13\"")]
    [InlineData("sslProtocols=\"tls12, Tls13\"")]
    public void Schema_TakesWhatTheEngineTakes(string attributes)
        => Validate(attributes).Should().BeEmpty();

    [Theory]
    [InlineData("ackMode=\"sometimes\"")]
    [InlineData("sslProtocols=\"Tls99\"")]
    [InlineData("sslProtocols=\"Tls12,\"")]
    public void Schema_RefusesWhatTheEngineRefuses(string attributes)
        => Validate(attributes).Should().NotBeEmpty();

    [Fact]
    public void Schema_LongPickOneList_TakesTheNamesTheCatalogLists_AndNoOtherCase()
    {
        // The one deliberate exception (see the class comment): the case-insensitive union for the
        // catalog's 43 long pick-one lists is what took the editor's language server past its memory
        // on the first completion (2026-10-08). The engine goes on parsing any case, the editor asks
        // for the spelling the catalog carries.
        Validate("fileExist=\"Append\"").Should().BeEmpty();
        Validate("fileExist=\"append\"").Should().NotBeEmpty();
    }
}
