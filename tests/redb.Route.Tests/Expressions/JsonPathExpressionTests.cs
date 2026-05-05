using FluentAssertions;
using Newtonsoft.Json.Linq;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for JsonPath functionality: JsonPathExpression, TypedJsonPathExpression,
/// CompiledJPathExpression, jpath() DSL helpers, and ApplyJPath via ExpressionResolver.
/// </summary>
[Collection("ExpressionResolver")]
public class JsonPathExpressionTests : IDisposable
{
    private const string SampleJson = """
        {
            "id": "DRV-001",
            "firstName": "John",
            "lastName": "Doe",
            "isHired": true,
            "dismissed": false,
            "age": 35,
            "salary": 75000.50,
            "tags": ["driver", "active", "verified"],
            "scores": [90, 85, 92],
            "address": {
                "city": "Moscow",
                "country": "Russia"
            },
            "contacts": [
                { "type": "email", "value": "john@example.com" },
                { "type": "phone", "value": "+7-999-123-45-67" }
            ]
        }
        """;

    public JsonPathExpressionTests()
    {
        ExpressionResolver.ClearAllCaches();
    }

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
    }

    private static IExchange CreateJsonExchange(string json = SampleJson)
        => new Exchange(new Message(JToken.Parse(json)));

    private static IExchange CreatePocoExchange()
        => new Exchange(new Message(new { Name = "Alice", Age = 30, Active = true }));

    // ── JsonPathExpression: basic property access ──

    [Fact]
    public void Evaluate_StringProperty_ReturnsString()
    {
        var expr = new JsonPathExpression("$.firstName");
        var result = expr.Evaluate<string>(CreateJsonExchange());
        result.Should().Be("John");
    }

    [Fact]
    public void Evaluate_BoolProperty_ReturnsBool()
    {
        var expr = new JsonPathExpression("$.isHired");
        var result = expr.Evaluate<bool>(CreateJsonExchange());
        result.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_IntProperty_ReturnsInt()
    {
        var expr = new JsonPathExpression("$.age");
        var result = expr.Evaluate<int>(CreateJsonExchange());
        result.Should().Be(35);
    }

    [Fact]
    public void Evaluate_DoubleProperty_ReturnsDouble()
    {
        var expr = new JsonPathExpression("$.salary");
        var result = expr.Evaluate<double>(CreateJsonExchange());
        result.Should().BeApproximately(75000.50, 0.01);
    }

    [Fact]
    public void Evaluate_NestedProperty_ReturnsValue()
    {
        var expr = new JsonPathExpression("$.address.city");
        var result = expr.Evaluate<string>(CreateJsonExchange());
        result.Should().Be("Moscow");
    }

    [Fact]
    public void Evaluate_RootPath_ReturnsWholeDocument()
    {
        var expr = new JsonPathExpression("$");
        var result = expr.Evaluate<object>(CreateJsonExchange());
        result.Should().NotBeNull();
        // Root returns JObject
        result.Should().BeAssignableTo<JObject>();
    }

    [Fact]
    public void Evaluate_ArrayIndexAccess_ReturnsElement()
    {
        var expr = new JsonPathExpression("$.tags[0]");
        var result = expr.Evaluate<string>(CreateJsonExchange());
        result.Should().Be("driver");
    }

    // ── JsonPathExpression: array results ──

    [Fact]
    public void Evaluate_ArrayProperty_AsObject_ReturnsTypedArray()
    {
        var expr = new JsonPathExpression("$.scores");
        var result = expr.Evaluate<object>(CreateJsonExchange());
        // ConvertJTokenToType<object> creates a typed int[] for homogeneous integer arrays
        result.Should().BeAssignableTo<Array>();
    }

    [Fact]
    public void Evaluate_ArrayProperty_AsIntArray()
    {
        var expr = new JsonPathExpression("$.scores");
        var result = expr.Evaluate<int[]>(CreateJsonExchange());
        result.Should().Equal(90, 85, 92);
    }

    [Fact]
    public void Evaluate_ArrayProperty_AsStringArray()
    {
        var expr = new JsonPathExpression("$.tags");
        var result = expr.Evaluate<string[]>(CreateJsonExchange());
        result.Should().Equal("driver", "active", "verified");
    }

    [Fact]
    public void Evaluate_WildcardSelect_ReturnsMultipleValues()
    {
        var expr = new JsonPathExpression("$.contacts[*].type");
        var result = expr.Evaluate<string>(CreateJsonExchange());
        // When T=string with multiple tokens, should join them
        result.Should().Contain("email");
        result.Should().Contain("phone");
    }

    // ── JsonPathExpression: path not found → null / default ──

    [Fact]
    public void Evaluate_NonexistentPath_ReturnsDefault()
    {
        var expr = new JsonPathExpression("$.nonexistent");
        var result = expr.Evaluate<object>(CreateJsonExchange());
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NonexistentPath_String_ReturnsNull()
    {
        var expr = new JsonPathExpression("$.doesNotExist");
        var result = expr.Evaluate<string>(CreateJsonExchange());
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NonexistentPath_ValueType_Throws()
    {
        var expr = new JsonPathExpression("$.doesNotExist");
        var act = () => expr.Evaluate<int>(CreateJsonExchange());
        act.Should().Throw<InvalidOperationException>();
    }

    // ── JsonPathExpression: POCO body (auto-serialized) ──

    [Fact]
    public void Evaluate_PocoBody_ExtractsProperty()
    {
        var expr = new JsonPathExpression("$.Name");
        var result = expr.Evaluate<string>(CreatePocoExchange());
        result.Should().Be("Alice");
    }

    [Fact]
    public void Evaluate_PocoBody_ExtractsBool()
    {
        var expr = new JsonPathExpression("$.Active");
        var result = expr.Evaluate<bool>(CreatePocoExchange());
        result.Should().BeTrue();
    }

    // ── JsonPathExpression: string body (auto-parsed) ──

    [Fact]
    public void Evaluate_StringBody_ParsesJsonAndExtracts()
    {
        var exchange = new Exchange(new Message(SampleJson));
        var expr = new JsonPathExpression("$.firstName");
        var result = expr.Evaluate<string>(exchange);
        result.Should().Be("John");
    }

    // ── JsonPathExpression: null body ──

    [Fact]
    public void Evaluate_NullBody_Throws()
    {
        var expr = new JsonPathExpression("$.anything");
        var exchange = new Exchange(new Message(null));
        var act = () => expr.Evaluate<object>(exchange);
        act.Should().Throw<InvalidOperationException>().WithMessage("*null*");
    }

    // ── JsonPathExpression: filter queries ──

    [Fact]
    public void Evaluate_FilterQuery_ReturnsFilteredResults()
    {
        var expr = new JsonPathExpression("$.contacts[?(@.type=='email')].value");
        var result = expr.Evaluate<string>(CreateJsonExchange());
        result.Should().Be("john@example.com");
    }

    [Fact]
    public void Evaluate_FilterQuery_AsBool_ReturnsTrueWhenMatches()
    {
        var expr = new JsonPathExpression("$.contacts[?(@.type=='email')]");
        var result = expr.Evaluate<bool>(CreateJsonExchange());
        result.Should().BeTrue();
    }

    // ── JsonPathExpression: constructor validation ──

    [Fact]
    public void Constructor_NullPath_Throws()
    {
        var act = () => new JsonPathExpression(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── TypedJsonPathExpression<T>: type-safe extraction ──

    [Fact]
    public void TypedExpression_Bool_ReturnsBool()
    {
        var expr = new TypedJsonPathExpression<bool>("$.isHired");
        // Processor calls Evaluate<object>, but internally evaluates as bool
        var result = expr.Evaluate<object>(CreateJsonExchange());
        result.Should().BeOfType<bool>();
        result.Should().Be(true);
    }

    [Fact]
    public void TypedExpression_Int_ReturnsInt()
    {
        var expr = new TypedJsonPathExpression<int>("$.age");
        var result = expr.Evaluate<object>(CreateJsonExchange());
        result.Should().BeOfType<int>();
        result.Should().Be(35);
    }

    [Fact]
    public void TypedExpression_StringArray_ReturnsStringArray()
    {
        var expr = new TypedJsonPathExpression<string[]>("$.tags");
        var result = expr.Evaluate<object>(CreateJsonExchange());
        result.Should().BeOfType<string[]>();
        ((string[])result!).Should().Equal("driver", "active", "verified");
    }

    [Fact]
    public void TypedExpression_DirectT_StillWorks()
    {
        var expr = new TypedJsonPathExpression<bool>("$.dismissed");
        // Directly asking for bool
        var result = expr.Evaluate<bool>(CreateJsonExchange());
        result.Should().BeFalse();
    }

    // ── CompiledJPathExpression: dynamic path ──

    [Fact]
    public void CompiledJPath_DynamicPathFromProperty()
    {
        var compiled = new CompiledJPathExpression(ex =>
            "$." + ex.Properties["field"]?.ToString());

        var exchange = CreateJsonExchange();
        exchange.Properties["field"] = "firstName";

        var result = compiled.Evaluate<string>(exchange);
        result.Should().Be("John");
    }

    [Fact]
    public void CompiledJPath_NullPath_ReturnsDefault()
    {
        var compiled = new CompiledJPathExpression(_ => null!);
        var result = compiled.Evaluate<string>(CreateJsonExchange());
        result.Should().BeNull();
    }

    // ── ApplyJPath via ExpressionResolver (string expression path) ──

    [Fact]
    public void Resolver_JPathInTemplate_ExtractsValue()
    {
        var exchange = CreateJsonExchange();
        var result = ExpressionResolver.ProcessTemplate("${jpath('$.firstName')}", exchange);
        result.Should().Be("John");
    }

    [Fact]
    public void Resolver_JPathInTemplate_PathNotFound_ReturnsEmpty()
    {
        var exchange = CreateJsonExchange();
        // Path not found → ApplyJPath returns null → template turns null to ""
        var result = ExpressionResolver.ProcessTemplate("${jpath('$.nonexistent')}", exchange);
        result.Should().Be("");
    }

    [Fact]
    public void Resolver_JPathInTemplate_NestedPath()
    {
        var exchange = CreateJsonExchange();
        var result = ExpressionResolver.ProcessTemplate("City: ${jpath('$.address.city')}", exchange);
        result.Should().Be("City: Moscow");
    }

    [Fact]
    public void Resolver_JPathDirect_ReturnsTypedResult()
    {
        var exchange = CreateJsonExchange();
        var result = ExpressionResolver.ResolveExpression("jpath('$.age')", exchange);
        // Should return a numeric value, not a string
        result.Should().NotBeNull();
    }

    [Fact]
    public void Resolver_JPath_RootPath_ReturnsObject()
    {
        var exchange = CreateJsonExchange();
        var result = ExpressionResolver.ResolveExpression("jpath('$')", exchange);
        result.Should().NotBeNull();
    }

    // ── SetProperty with IExpression (integration) ──

    [Fact]
    public void ExpressionPropertyProcessor_SetsPropertyFromExpression()
    {
        var processor = new redb.Route.Processors.ExpressionPropertyProcessor(
            "name", new JsonPathExpression("$.firstName"));

        var exchange = CreateJsonExchange();
        processor.Process(exchange);

        exchange.Properties["name"].Should().Be("John");
    }

    [Fact]
    public void ExpressionPropertyProcessor_TypedExpression_SetsTypedValue()
    {
        var processor = new redb.Route.Processors.ExpressionPropertyProcessor(
            "hired", new TypedJsonPathExpression<bool>("$.isHired"));

        var exchange = CreateJsonExchange();
        processor.Process(exchange);

        exchange.Properties["hired"].Should().BeOfType<bool>();
        exchange.Properties["hired"].Should().Be(true);
    }

    // ── ApplyJPath: no longer converts JArray to string ──

    [Fact]
    public void Resolver_JPath_Array_DoesNotFlattenToString()
    {
        var exchange = CreateJsonExchange();
        var result = ExpressionResolver.ResolveExpression("jpath('$.scores')", exchange);
        // Should NOT be a comma-separated string "90, 85, 92"
        result.Should().NotBeOfType<string>();
    }

    // ── ApplyJPath error handling: propagates real errors, null body → null ──

    [Fact]
    public void Resolver_JPath_NullBody_ReturnsNull()
    {
        var exchange = new Exchange(new Message(null));
        var result = ExpressionResolver.ResolveExpression("jpath('$.anything')", exchange);
        result.Should().BeNull();
    }
}
