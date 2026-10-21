using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// A header filter strategy declared in markup: <see cref="DefaultHeaderFilterStrategy"/> is a plain bean whose name
/// lists are filled from <c>&lt;list&gt;</c>, and an endpoint's <c>headerFilterStrategy=#name</c> finds it.
/// </summary>
public class XmlHeaderFilterStrategyBeanTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string T<TType>() => $"{typeof(TType).FullName}, {typeof(TType).Assembly.GetName().Name}";

    [Fact]
    public void AWhitelistDeclaredInMarkup_IsBuiltAndFoundByAnEndpoint()
    {
        _context.AddXmlContextFromContent($"""
            <context xmlns="urn:redb:route:1.0">
              <bean name="correlationOnly" type="{T<DefaultHeaderFilterStrategy>()}">
                <property key="OutFilter"><list><value>Correlation-ID</value><value>traceparent</value></list></property>
                <property key="FilterOnMatch" value="false"/>
              </bean>
            </context>
            """);

        var strategy = _context.GetFromRegistry<IHeaderFilterStrategy>("correlationOnly")!;
        var exchange = new Exchange(new Message("body"));
        strategy.FilterOutgoing("Correlation-ID", "c-42", exchange).Should().BeFalse("a listed header goes out");
        strategy.FilterOutgoing("correlation-id", "c-42", exchange).Should().BeFalse("names match case-insensitively");
        strategy.FilterOutgoing("Authorization", "Bearer x", exchange).Should().BeTrue("anything else stays back");

        var act = () => _context.GetEndpoint("vm://xml-strategy?headerFilterStrategy=#correlationOnly").CreateProducer();
        act.Should().NotThrow("the endpoint resolves the bean declared in markup");
    }

    [Fact]
    public void TheGenerator_CarriesTheBeanAndTheOptionIntoCSharp()
    {
        var document = System.Xml.Linq.XDocument.Parse($"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="correlationOnly" type="{T<DefaultHeaderFilterStrategy>()}">
                <property key="OutFilter"><list><value>Correlation-ID</value></list></property>
                <property key="FilterOnMatch" value="false"/>
              </bean>
              <route id="send">
                <from uri="direct://in"/>
                <to uri="vm://out?headerFilterStrategy=#correlationOnly"/>
              </route>
            </routes>
            """);

        var code = XmlCodeGenerator.Generate(document, "StrategyGenerated", "Tests.Generated");

        code.Should().Contain($"XmlBeans.Create(Context!, \"{T<DefaultHeaderFilterStrategy>()}\"")
            .And.Contain("(\"OutFilter\", XmlBeans.List(Context!, null, [\"Correlation-ID\"]))")
            .And.Contain("(\"FilterOnMatch\", \"false\")")
            .And.Contain("vm://out?headerFilterStrategy=#correlationOnly");
    }
}
