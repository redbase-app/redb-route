using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>One partner of a node: registered on its own, listed by the node.</summary>
public class ProbePartner
{
    public string? Id { get; set; }
}

/// <summary>A node holding its partners in every collection shape a list builds into.</summary>
public class ProbeNode
{
    public List<ProbePartner> Partners { get; set; } = [];
    public ProbePartner[]? PartnerArray { get; set; }
    public IReadOnlyList<ProbePartner>? ReadOnlyPartners { get; set; }
    public IEnumerable<int>? Ports { get; set; }
    public List<List<string>>? Groups { get; set; }
    public ProbePartner? Signing { get; set; }
    public string? Title { get; set; }
}

/// <summary>Partners through the constructor, and through a static creator taking a List.</summary>
public class ProbeRoster(IReadOnlyList<ProbePartner> partners)
{
    public IReadOnlyList<ProbePartner> Partners => partners;
    public static ProbeRoster Of(List<ProbePartner> partners) => new(partners);
}

/// <summary>
/// &lt;bean&gt; lists and references: a node that lists partners registered on their own (the AS4
/// shape: each partner its own bean, rotation a second slot) is built from XML, into the declared
/// collection type, and a reference may only look back — never forward, never at nothing.
/// </summary>
public class XmlBeanListAndRefTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string T<TType>() => $"{typeof(TType).FullName}, {typeof(TType).Assembly.GetName().Name}";

    private static string Partners => $"""
          <bean name="acme" type="{T<ProbePartner>()}"><property key="Id" value="acme"/></bean>
          <bean name="globex" type="{T<ProbePartner>()}"><property key="Id" value="globex"/></bean>
        """;

    private void LoadContext(string beans)
        => _context.AddXmlContextFromContent($"""
            <context xmlns="urn:redb:route:1.0">
            {beans}
            </context>
            """);

    // ── building ─────────────────────────────────────────────────────────────

    [Fact]
    public void Property_List_OfRefsAndAnInlineBean_BuildsEveryCollectionShape()
    {
        _context.SetProperty("as4.partner3.id", "initech");
        _context.SetProperty("as4.port", "8443");
        LoadContext(Partners + $"""
            <bean name="node" type="{T<ProbeNode>()}">
              <property key="Partners">
                <list>
                  <ref bean="acme"/>
                  <ref bean="globex"/>
                  <bean type="{T<ProbePartner>()}"><property key="Id" value="{"{{"}as4.partner3.id{"}}"}"/></bean>
                </list>
              </property>
              <property key="PartnerArray"><list><ref bean="globex"/></list></property>
              <property key="ReadOnlyPartners"><list><ref bean="acme"/><ref bean="globex"/></list></property>
              <property key="Ports"><list><value>{"{{"}as4.port{"}}"}</value><value>8080</value></list></property>
              <property key="Groups"><list><list><value>a</value><value>b</value></list><list/></list></property>
            </bean>
            """);

        var node = _context.GetFromRegistry<ProbeNode>("node")!;
        var acme = _context.GetFromRegistry<ProbePartner>("acme");
        var globex = _context.GetFromRegistry<ProbePartner>("globex");

        node.Partners.Should().HaveCount(3);
        node.Partners[0].Should().BeSameAs(acme, "a reference is the registered instance, not a copy");
        node.Partners[1].Should().BeSameAs(globex);
        node.Partners[2].Id.Should().Be("initech", "an inline bean resolves its placeholders like any other");
        node.PartnerArray.Should().ContainSingle().Which.Should().BeSameAs(globex);
        node.ReadOnlyPartners.Should().Equal(acme!, globex!);
        node.Ports.Should().Equal(8443, 8080);
        node.Groups.Should().HaveCount(2);
        node.Groups![0].Should().Equal("a", "b");
        node.Groups[1].Should().BeEmpty();
    }

    [Fact]
    public void Property_RefAttribute_AssignsTheRegisteredBean()
    {
        LoadContext(Partners + $"""
            <bean name="node" type="{T<ProbeNode>()}">
              <property key="Signing" ref="acme"/>
            </bean>
            """);

        _context.GetFromRegistry<ProbeNode>("node")!.Signing
            .Should().BeSameAs(_context.GetFromRegistry<ProbePartner>("acme"));
    }

    [Fact]
    public void ConstructorArg_ListWithOf_BuildsTheArrayTheConstructorTakes()
    {
        LoadContext(Partners + $"""
            <bean name="roster" type="{T<ProbeRoster>()}">
              <constructorArg>
                <list of="{T<ProbePartner>()}"><ref bean="acme"/><ref bean="globex"/></list>
              </constructorArg>
            </bean>
            """);

        _context.GetFromRegistry<ProbeRoster>("roster")!.Partners.Select(p => p.Id).Should().Equal("acme", "globex");
    }

    [Fact]
    public void FactoryMethod_ListArgument_IsTypedByTheParameter()
    {
        LoadContext(Partners + $"""
            <bean name="roster" type="{T<ProbeRoster>()}" factoryMethod="Of">
              <constructorArg><list><ref bean="globex"/></list></constructorArg>
            </bean>
            """);

        _context.GetFromRegistry<ProbeRoster>("roster")!.Partners.Should().ContainSingle().Which.Id.Should().Be("globex");
    }

    [Fact]
    public void ReferenceFromTheContextFile_ReachesABeanOfARoutesFile()
    {
        // The context file loads first, so a routes-file bean may point back at it.
        LoadContext(Partners);
        _context.AddXmlRoutesFromContent($"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="node" type="{T<ProbeNode>()}"><property key="Signing" ref="globex"/></bean>
            </routes>
            """);

        _context.GetFromRegistry<ProbeNode>("node")!.Signing!.Id.Should().Be("globex");
    }

    // ── refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void ForwardReference_IsAPositionedError_NotANull()
    {
        var act = () => LoadContext($"""
            <bean name="node" type="{T<ProbeNode>()}">
              <property key="Signing" ref="late"/>
            </bean>
            <bean name="late" type="{T<ProbePartner>()}"/>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*(3,*bean reference 'late' names nothing registered so far*");
    }

    [Fact]
    public void UnknownReferenceInsideAList_IsAPositionedError()
    {
        var act = () => LoadContext($"""
            <bean name="node" type="{T<ProbeNode>()}">
              <property key="Partners"><list><ref bean="nobody"/></list></property>
            </bean>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*bean reference 'nobody' names nothing registered so far*");
    }

    [Fact]
    public void ListItemOfTheWrongType_IsRefused_NamingTheItem()
    {
        var act = () => LoadContext($"""
            <bean name="node" type="{T<ProbeNode>()}">
              <property key="Partners">
                <list><bean type="{T<ProbeOptions>()}"/></list>
              </property>
            </bean>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage($"*'{typeof(ProbeOptions).FullName}' is not assignable to ProbePartner for 'Partners[0]'*");
    }

    [Fact]
    public void ListIntoAPropertyThatIsNoCollection_IsRefused()
    {
        var act = () => LoadContext($"""
            <bean name="node" type="{T<ProbeNode>()}">
              <property key="Title"><list><value>x</value></list></property>
            </bean>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*'Title' is String, which a <list> does not build into*");
    }

    [Fact]
    public void ListOf_DifferingFromThePropertyElementType_IsRefused()
    {
        var act = () => LoadContext($"""
            <bean name="node" type="{T<ProbeNode>()}">
              <property key="Partners"><list of="{T<ProbeOptions>()}"/></property>
            </bean>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*differs from the element type*of 'Partners'*");
    }

    [Fact]
    public void ConstructorList_WithoutOf_IsRefused()
    {
        var act = () => LoadContext(Partners + $"""
            <bean name="roster" type="{T<ProbeRoster>()}">
              <constructorArg><list><ref bean="acme"/></list></constructorArg>
            </bean>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*'constructorArg 1': a <list> passed to a constructor names its element type with of=*");
    }

    [Fact]
    public void RefWithAHash_IsRefused_OneSpellingOnly()
    {
        var act = () => LoadContext(Partners + $"""
            <bean name="node" type="{T<ProbeNode>()}"><property key="Signing" ref="#acme"/></bean>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*a bean reference is the bare name: 'acme', not '#acme'*");
    }

    [Fact]
    public void SlotWithTwoForms_IsRefused()
    {
        var act = () => LoadContext(Partners + $"""
            <bean name="node" type="{T<ProbeNode>()}"><property key="Signing" ref="acme" value="x"/></bean>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*<property> takes exactly one of: value=, ref=*");
    }

    [Fact]
    public void ValueOutsideAList_IsRefused()
    {
        var act = () => LoadContext($"""
            <bean name="node" type="{T<ProbeNode>()}"><property key="Title"><value>x</value></property></bean>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*<value> belongs inside a <list>*");
    }

    [Fact]
    public void Schema_AcceptsListsAndReferences_AndRejectsAForeignListItem()
    {
        var valid = XDocument.Parse($"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="n" type="X">
                <property key="P" ref="a"/>
                <property key="L"><list of="Y"><value>1</value><ref bean="a"/><bean type="Z"/><list/></list></property>
                <constructorArg ref="a"/>
              </bean>
            </routes>
            """, LoadOptions.SetLineInfo);
        XmlRouteSchema.Validate(valid, ElementRegistry.CreateDefault()).Should().BeEmpty();

        var invalid = XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="n" type="X"><property key="L"><list><item/></list></property></bean>
            </routes>
            """, LoadOptions.SetLineInfo);
        XmlRouteSchema.Validate(invalid, ElementRegistry.CreateDefault()).Should().NotBeEmpty();
    }

    // ── generated C# ─────────────────────────────────────────────────────────

    [Fact]
    public void Generator_PrintsReferencesAndLists()
    {
        var document = XDocument.Parse($"""
            <routes xmlns="urn:redb:route:1.0">
              {Partners}
              <bean name="node" type="{T<ProbeNode>()}">
                <property key="Partners"><list><ref bean="acme"/><value>{"{{"}x{"}}"}</value></list></property>
                <property key="Signing" ref="globex"/>
              </bean>
              <bean name="roster" type="{T<ProbeRoster>()}">
                <constructorArg><list of="{T<ProbePartner>()}"><ref bean="acme"/></list></constructorArg>
              </bean>
            </routes>
            """);

        var code = XmlCodeGenerator.Generate(document, "ListGenerated", "Tests.Generated");

        code.Should().Contain("(\"Partners\", XmlBeans.List(Context!, null, [XmlBeans.Ref(Context!, \"acme\"), \"{{x}}\"]))")
            .And.Contain("(\"Signing\", XmlBeans.Ref(Context!, \"globex\"))")
            .And.Contain($"XmlBeans.List(Context!, \"{T<ProbePartner>()}\", [XmlBeans.Ref(Context!, \"acme\")])");
    }

    [Fact]
    public void GeneratedCalls_BuildWhatTheXmlSectionBuilds()
    {
        // Exactly the calls the generator prints above, run against a context.
        _context.AddToRegistry("acme", XmlBeans.Create(_context, T<ProbePartner>(), [], [("Id", "acme")]));
        _context.AddToRegistry("node", XmlBeans.Create(_context, T<ProbeNode>(), [],
        [
            ("Partners", XmlBeans.List(_context, null, [XmlBeans.Ref(_context, "acme")])),
            ("Signing", XmlBeans.Ref(_context, "acme")),
        ]));
        var roster = (ProbeRoster)XmlBeans.Create(_context, T<ProbeRoster>(),
            [XmlBeans.List(_context, T<ProbePartner>(), [XmlBeans.Ref(_context, "acme")])], []);

        var acme = _context.GetFromRegistry<ProbePartner>("acme");
        _context.GetFromRegistry<ProbeNode>("node")!.Partners.Should().ContainSingle().Which.Should().BeSameAs(acme);
        _context.GetFromRegistry<ProbeNode>("node")!.Signing.Should().BeSameAs(acme);
        roster.Partners.Should().ContainSingle().Which.Should().BeSameAs(acme);

        var forward = () => XmlBeans.Ref(_context, "late");
        forward.Should().Throw<InvalidOperationException>().WithMessage("*'late' names nothing registered so far*");
    }
}
