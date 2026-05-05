using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Serialization;
using XPathExpr = redb.Route.Expressions.XPathExpression;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for XML/XPath functionality: XPathExpression, TypedXPathExpression,
/// CompiledXPathExpression, xpath() DSL helpers, ApplyXPath via ExpressionResolver,
/// and XmlMessageSerializer.
/// </summary>
[Collection("ExpressionResolver")]
public class XPathExpressionTests : IDisposable
{
    private const string SampleXml = """
        <driver>
            <id>DRV-001</id>
            <firstName>John</firstName>
            <lastName>Doe</lastName>
            <isHired>true</isHired>
            <dismissed>false</dismissed>
            <age>35</age>
            <salary>75000.50</salary>
            <tags>
                <tag>driver</tag>
                <tag>active</tag>
                <tag>verified</tag>
            </tags>
            <scores>
                <score>90</score>
                <score>85</score>
                <score>92</score>
            </scores>
            <address>
                <city>Moscow</city>
                <country>Russia</country>
            </address>
            <contacts>
                <contact type="email" value="john@example.com"/>
                <contact type="phone" value="+7-999-123-45-67"/>
            </contacts>
        </driver>
        """;

    public XPathExpressionTests()
    {
        ExpressionResolver.ClearAllCaches();
    }

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
    }

    private static IExchange CreateXmlExchange(string xml = SampleXml)
        => new Exchange(new Message(xml));

    private static IExchange CreateXDocumentExchange()
        => new Exchange(new Message(XDocument.Parse(SampleXml)));

    private static IExchange CreateXElementExchange()
        => new Exchange(new Message(XDocument.Parse(SampleXml).Root!));

    // ── XPathExpression: basic element access ──

    [Fact]
    public void Evaluate_StringElement_ReturnsString()
    {
        var expr = new XPathExpr("/driver/firstName");
        var result = expr.Evaluate<string>(CreateXmlExchange());
        result.Should().Be("John");
    }

    [Fact]
    public void Evaluate_BoolElement_ReturnsBool()
    {
        var expr = new XPathExpr("/driver/isHired");
        var result = expr.Evaluate<bool>(CreateXmlExchange());
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_BoolFalseElement_ReturnsFalse()
    {
        var expr = new XPathExpr("/driver/dismissed");
        var result = expr.Evaluate<bool>(CreateXmlExchange());
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_IntElement_ReturnsInt()
    {
        var expr = new XPathExpr("/driver/age");
        var result = expr.Evaluate<int>(CreateXmlExchange());
        result.Should().Be(35);
    }

    [Fact]
    public void Evaluate_DoubleElement_ReturnsDouble()
    {
        var expr = new XPathExpr("/driver/salary");
        var result = expr.Evaluate<double>(CreateXmlExchange());
        result.Should().Be(75000.50);
    }

    [Fact]
    public void Evaluate_NestedElement_ReturnsValue()
    {
        var expr = new XPathExpr("/driver/address/city");
        var result = expr.Evaluate<string>(CreateXmlExchange());
        result.Should().Be("Moscow");
    }

    // ── Attribute access ──

    [Fact]
    public void Evaluate_Attribute_ReturnsValue()
    {
        var expr = new XPathExpr("/driver/contacts/contact[@type='email']/@value");
        var result = expr.Evaluate<string>(CreateXmlExchange());
        result.Should().Be("john@example.com");
    }

    [Fact]
    public void Evaluate_AttributeSelector_ReturnsCorrectContact()
    {
        var expr = new XPathExpr("/driver/contacts/contact[@type='phone']/@value");
        var result = expr.Evaluate<string>(CreateXmlExchange());
        result.Should().Be("+7-999-123-45-67");
    }

    // ── Multiple elements ──

    [Fact]
    public void Evaluate_MultipleElements_ReturnsStringArray()
    {
        var expr = new XPathExpr("/driver/tags/tag");
        var result = expr.Evaluate<string[]>(CreateXmlExchange());
        result.Should().BeEquivalentTo("driver", "active", "verified");
    }

    [Fact]
    public void Evaluate_MultipleElements_JoinedString()
    {
        var expr = new XPathExpr("/driver/tags/tag");
        var result = expr.Evaluate<string>(CreateXmlExchange());
        result.Should().Be("driver, active, verified");
    }

    [Fact]
    public void Evaluate_MultipleScores_ReturnsIntArray()
    {
        var expr = new XPathExpr("/driver/scores/score");
        var result = expr.Evaluate<int[]>(CreateXmlExchange());
        result.Should().BeEquivalentTo(new[] { 90, 85, 92 });
    }

    [Fact]
    public void Evaluate_MultipleElements_AsXElements()
    {
        var expr = new XPathExpr("/driver/tags/tag");
        var result = expr.Evaluate<XElement[]>(CreateXmlExchange());
        result.Should().HaveCount(3);
        result[0].Value.Should().Be("driver");
    }

    // ── XPath functions ──

    [Fact]
    public void Evaluate_CountFunction_ReturnsInt()
    {
        var expr = new XPathExpr("count(/driver/tags/tag)");
        var result = expr.Evaluate<int>(CreateXmlExchange());
        result.Should().Be(3);
    }

    [Fact]
    public void Evaluate_StringFunction_ReturnsString()
    {
        var expr = new XPathExpr("string(/driver/firstName)");
        var result = expr.Evaluate<string>(CreateXmlExchange());
        result.Should().Be("John");
    }

    [Fact]
    public void Evaluate_BooleanFunction_ReturnsBool()
    {
        // boolean() on non-empty string returns true
        var expr = new XPathExpr("boolean(/driver/firstName)");
        var result = expr.Evaluate<bool>(CreateXmlExchange());
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_BooleanFunctionOnMissing_ReturnsFalse()
    {
        var expr = new XPathExpr("boolean(/driver/nonExistent)");
        var result = expr.Evaluate<bool>(CreateXmlExchange());
        result.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_SumFunction_ReturnsDouble()
    {
        var expr = new XPathExpr("sum(/driver/scores/score)");
        var result = expr.Evaluate<double>(CreateXmlExchange());
        result.Should().Be(267);
    }

    [Fact]
    public void Evaluate_CountAsObject_ReturnsInt()
    {
        // count() returns double from XPath, but Evaluate<object> should smart-convert to int
        var expr = new XPathExpr("count(/driver/tags/tag)");
        var result = expr.Evaluate<object>(CreateXmlExchange());
        result.Should().Be(3); // int, not double
    }

    // ── Path not found ──

    [Fact]
    public void Evaluate_NotFound_ReturnsNull_ForString()
    {
        var expr = new XPathExpr("/driver/nonExistent");
        var result = expr.Evaluate<string>(CreateXmlExchange());
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NotFound_Throws_ForValueType()
    {
        var expr = new XPathExpr("/driver/nonExistent");
        var act = () => expr.Evaluate<int>(CreateXmlExchange());
        act.Should().Throw<InvalidOperationException>();
    }

    // ── XDocument body ──

    [Fact]
    public void Evaluate_XDocumentBody_Works()
    {
        var expr = new XPathExpr("/driver/id");
        var result = expr.Evaluate<string>(CreateXDocumentExchange());
        result.Should().Be("DRV-001");
    }

    // ── XElement body ──

    [Fact]
    public void Evaluate_XElementBody_Works()
    {
        var expr = new XPathExpr("/driver/lastName");
        var result = expr.Evaluate<string>(CreateXElementExchange());
        result.Should().Be("Doe");
    }

    // ── POCO body (auto-serialized) ──

    [Fact]
    public void Evaluate_PocoBody_Serialized()
    {
        var exchange = new Exchange(new Message(new TestPoco { Name = "Alice", Age = 30 }));
        var expr = new XPathExpr("//Name");
        var result = expr.Evaluate<string>(exchange);
        result.Should().Be("Alice");
    }

    [Fact]
    public void Evaluate_PocoBody_IntField()
    {
        var exchange = new Exchange(new Message(new TestPoco { Name = "Bob", Age = 42 }));
        var expr = new XPathExpr("//Age");
        var result = expr.Evaluate<int>(exchange);
        result.Should().Be(42);
    }

    // ── Null body ──

    [Fact]
    public void Evaluate_NullBody_Throws()
    {
        var exchange = new Exchange(new Message(null));
        var expr = new XPathExpr("/root");
        var act = () => expr.Evaluate<string>(exchange);
        act.Should().Throw<InvalidOperationException>().WithMessage("*null*");
    }

    // ── Empty string body ──

    [Fact]
    public void Evaluate_EmptyStringBody_Throws()
    {
        var exchange = new Exchange(new Message(""));
        var expr = new XPathExpr("/root");
        var act = () => expr.Evaluate<string>(exchange);
        act.Should().Throw<InvalidOperationException>().WithMessage("*empty string*");
    }

    // ── Invalid XML body ──

    [Fact]
    public void Evaluate_InvalidXml_Throws()
    {
        var exchange = new Exchange(new Message("not xml at all"));
        var expr = new XPathExpr("/root");
        var act = () => expr.Evaluate<string>(exchange);
        act.Should().Throw<InvalidOperationException>().WithMessage("*XML parsing error*");
    }

    // ── Constructor validation ──

    [Fact]
    public void Constructor_NullPath_Throws()
    {
        var act = () => new XPathExpr(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── SetValue not supported ──

    [Fact]
    public void SetValue_Throws()
    {
        var expr = new XPathExpr("/root");
        var act = () => expr.SetValue(CreateXmlExchange(), "value");
        act.Should().Throw<NotSupportedException>();
    }

    // ── Evaluate<object> single element ──

    [Fact]
    public void EvaluateObject_SingleElement_ReturnsSmartParsed()
    {
        var expr = new XPathExpr("/driver/age");
        var result = expr.Evaluate<object>(CreateXmlExchange());
        result.Should().Be(35); // smart-parsed to int
    }

    [Fact]
    public void EvaluateObject_SingleStringElement_ReturnsString()
    {
        var expr = new XPathExpr("/driver/firstName");
        var result = expr.Evaluate<object>(CreateXmlExchange());
        result.Should().Be("John");
    }

    [Fact]
    public void EvaluateObject_MultipleElements_ReturnsStringArray()
    {
        var expr = new XPathExpr("/driver/tags/tag");
        var result = expr.Evaluate<object>(CreateXmlExchange());
        result.Should().BeOfType<string[]>();
        (result as string[]).Should().BeEquivalentTo("driver", "active", "verified");
    }

    // ── TypedXPathExpression<T> ──

    [Fact]
    public void TypedXPath_Bool_ReturnsBoxedBool()
    {
        var expr = new TypedXPathExpression<bool>("/driver/isHired");
        // Processor calls Evaluate<object> — TypedXPath must route to Evaluate<bool>
        var result = expr.Evaluate<object>(CreateXmlExchange());
        result.Should().BeOfType<bool>();
        result.Should().Be(true);
    }

    [Fact]
    public void TypedXPath_Int_ReturnsBoxedInt()
    {
        var expr = new TypedXPathExpression<int>("/driver/age");
        var result = expr.Evaluate<object>(CreateXmlExchange());
        result.Should().BeOfType<int>();
        result.Should().Be(35);
    }

    [Fact]
    public void TypedXPath_StringArray_ReturnsTypedArray()
    {
        var expr = new TypedXPathExpression<string[]>("/driver/tags/tag");
        var result = expr.Evaluate<object>(CreateXmlExchange());
        result.Should().BeOfType<string[]>();
        (result as string[]).Should().BeEquivalentTo("driver", "active", "verified");
    }

    [Fact]
    public void TypedXPath_DirectT_ForwardsCorrectly()
    {
        var expr = new TypedXPathExpression<string>("/driver/firstName");
        var result = expr.Evaluate<string>(CreateXmlExchange());
        result.Should().Be("John");
    }

    // ── CompiledXPathExpression ──

    [Fact]
    public void CompiledXPath_DynamicPath_Works()
    {
        var exchange = CreateXmlExchange();
        exchange.Properties["xpathQuery"] = "/driver/firstName";
        
        var compiled = new CompiledXPathExpression(ex => ex.getProperty<string>("xpathQuery")!);
        var result = compiled.Evaluate<string>(exchange);
        result.Should().Be("John");
    }

    [Fact]
    public void CompiledXPath_NullPath_ReturnsDefault()
    {
        var exchange = CreateXmlExchange();
        var compiled = new CompiledXPathExpression(_ => null!);
        var result = compiled.Evaluate<string>(exchange);
        result.Should().BeNull();
    }

    // ── ExpressionResolver template integration ──

    [Fact]
    public void ExpressionResolver_XPathTemplate_ResolvesCorrectly()
    {
        var exchange = CreateXmlExchange();
        var template = "Driver: ${xpath(/driver/firstName)}";
        var result = ExpressionResolver.ResolveExpression(template, exchange)?.ToString();
        result.Should().Be("Driver: John");
    }

    [Fact]
    public void ExpressionResolver_XPathDirectExpression_ResolvesCorrectly()
    {
        var exchange = CreateXmlExchange();
        var result = ExpressionResolver.ResolveExpression("xpath(/driver/age)", exchange);
        result.Should().Be(35);
    }

    [Fact]
    public void ExpressionResolver_XPathQuotedExpression_ResolvesCorrectly()
    {
        var exchange = CreateXmlExchange();
        var result = ExpressionResolver.ResolveExpression("xpath('/driver/lastName')", exchange);
        // Should return smart-parsed object — "Doe" is a string
        result.Should().Be("Doe");
    }

    // ── ApplyXPath via ExpressionResolver (null body) ──

    [Fact]
    public void ApplyXPath_NullBody_ReturnsNull()
    {
        var exchange = new Exchange(new Message(null));
        var result = ExpressionResolver.ResolveExpression("xpath(/root/child)", exchange);
        result.Should().BeNull();
    }

    // ── Namespace support ──

    [Fact]
    public void Evaluate_WithNamespace_Works()
    {
        const string nsXml = """
            <root xmlns:ns="http://example.com/ns">
                <ns:item>Hello</ns:item>
            </root>
            """;
        
        var nsMgr = new XmlNamespaceManager(new NameTable());
        nsMgr.AddNamespace("ns", "http://example.com/ns");
        
        var exchange = new Exchange(new Message(nsXml));
        var expr = new XPathExpr("/root/ns:item", nsMgr);
        var result = expr.Evaluate<string>(exchange);
        result.Should().Be("Hello");
    }

    // ── XmlMessageSerializer ──

    [Fact]
    public void XmlSerializer_Serialize_ProducesXml()
    {
        var serializer = new XmlMessageSerializer();
        var data = new TestPoco { Name = "Alice", Age = 30 };
        var bytes = serializer.Serialize(data);
        
        bytes.Should().NotBeEmpty();
        var xml = System.Text.Encoding.UTF8.GetString(bytes);
        xml.Should().Contain("<Name>Alice</Name>");
        xml.Should().Contain("<Age>30</Age>");
    }

    [Fact]
    public void XmlSerializer_Deserialize_ReturnsObject()
    {
        var serializer = new XmlMessageSerializer();
        var original = new TestPoco { Name = "Bob", Age = 42 };
        var bytes = serializer.Serialize(original);
        
        var restored = serializer.Deserialize<TestPoco>(bytes);
        restored.Should().NotBeNull();
        restored!.Name.Should().Be("Bob");
        restored.Age.Should().Be(42);
    }

    [Fact]
    public void XmlSerializer_DeserializeUntyped_ReturnsObject()
    {
        var serializer = new XmlMessageSerializer();
        var original = new TestPoco { Name = "Carol", Age = 50 };
        var bytes = serializer.Serialize(original);
        
        var restored = serializer.Deserialize(bytes, typeof(TestPoco)) as TestPoco;
        restored.Should().NotBeNull();
        restored!.Name.Should().Be("Carol");
    }

    [Fact]
    public void XmlSerializer_ContentType_IsApplicationXml()
    {
        var serializer = new XmlMessageSerializer();
        serializer.ContentType.Should().Be("application/xml");
    }

    [Fact]
    public void XmlSerializer_RoundTrip_PreservesData()
    {
        var serializer = new XmlMessageSerializer();
        var original = new TestPoco { Name = "Dave", Age = 28 };
        var bytes = serializer.Serialize(original);
        var restored = serializer.Deserialize<TestPoco>(bytes);
        
        restored!.Name.Should().Be(original.Name);
        restored.Age.Should().Be(original.Age);
    }

    [Fact]
    public void XmlSerializer_Deserialize_NullData_Throws()
    {
        var serializer = new XmlMessageSerializer();
        var act = () => serializer.Deserialize<TestPoco>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void XmlSerializer_DeserializeUntyped_NullData_Throws()
    {
        var serializer = new XmlMessageSerializer();
        var act = () => serializer.Deserialize(null!, typeof(TestPoco));
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Marshal/Unmarshal integration ──

    [Fact]
    public async Task MarshalProcessor_WithXmlSerializer_SetsContentType()
    {
        var serializer = new XmlMessageSerializer();
        var processor = new MarshalProcessor(serializer);
        var exchange = new Exchange(new Message(new TestPoco { Name = "Eve", Age = 33 }));
        
        await processor.Process(exchange);
        
        exchange.In.Body.Should().BeOfType<byte[]>();
        exchange.In.Headers["Content-Type"].Should().Be("application/xml");
    }

    [Fact]
    public async Task UnmarshalProcessor_WithXmlSerializer_RestoresObject()
    {
        var serializer = new XmlMessageSerializer();
        var marshalProcessor = new MarshalProcessor(serializer);
        var unmarshalProcessor = new UnmarshalProcessor(serializer, typeof(TestPoco));
        
        var original = new TestPoco { Name = "Frank", Age = 45 };
        var exchange = new Exchange(new Message(original));
        
        await marshalProcessor.Process(exchange);
        await unmarshalProcessor.Process(exchange);
        
        var restored = exchange.In.Body as TestPoco;
        restored.Should().NotBeNull();
        restored!.Name.Should().Be("Frank");
        restored.Age.Should().Be(45);
    }

    // ── ExpressionPropertyProcessor with XPath ──

    [Fact]
    public async Task ExpressionPropertyProcessor_WithXPath_SetsProperty()
    {
        var exchange = CreateXmlExchange();
        var expr = new XPathExpr("/driver/firstName");
        var processor = new redb.Route.Processors.ExpressionPropertyProcessor("driverName", expr);
        
        await processor.Process(exchange);
        
        exchange.Properties["driverName"].Should().Be("John");
    }

    // ── Descendant axis (//) ──

    [Fact]
    public void Evaluate_DescendantAxis_FindsNested()
    {
        var expr = new XPathExpr("//city");
        var result = expr.Evaluate<string>(CreateXmlExchange());
        result.Should().Be("Moscow");
    }

    // ── Predicate with index ──

    [Fact]
    public void Evaluate_IndexPredicate_ReturnsCorrect()
    {
        var expr = new XPathExpr("/driver/scores/score[2]");
        var result = expr.Evaluate<int>(CreateXmlExchange());
        result.Should().Be(85);
    }

    // ── XPath returning XElement ──

    [Fact]
    public void Evaluate_ReturnsXElement_WhenRequested()
    {
        var expr = new XPathExpr("/driver/address");
        var result = expr.Evaluate<XElement>(CreateXmlExchange());
        result.Should().NotBeNull();
        result.Name.LocalName.Should().Be("address");
        result.Element("city")!.Value.Should().Be("Moscow");
    }

    // ── Test POCO ──

    public class TestPoco
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }
}
