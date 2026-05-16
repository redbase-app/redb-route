using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class ResolveOptionTests
{
    // --- ResolveOption ---

    [Fact]
    public void ResolveOption_Null_ReturnsNull()
    {
        var opts = new TestEndpointOptions { Host = "any" };
        opts.ResolveOption(null, new Exchange()).Should().BeNull();
    }

    [Fact]
    public void ResolveOption_StaticString_ReturnsAsIs()
    {
        var opts = new TestEndpointOptions { Host = "any" };
        opts.ResolveOption("plain-value", new Exchange()).Should().Be("plain-value");
    }

    [Fact]
    public void ResolveOption_EmptyString_ReturnsEmpty()
    {
        var opts = new TestEndpointOptions { Host = "any" };
        opts.ResolveOption("", new Exchange()).Should().BeEmpty();
    }

    [Fact]
    public void ResolveOption_DollarWithoutBrace_ReturnsAsIs()
    {
        var opts = new TestEndpointOptions { Host = "any" };
        opts.ResolveOption("price$100", new Exchange()).Should().Be("price$100");
    }

    [Fact]
    public void ResolveOption_HeaderExpression_ResolvesFromExchange()
    {
        var opts = new TestEndpointOptions { Host = "any" };

        var msg = new Message("body");
        msg.Headers["region"] = "us-east";
        var exchange = new Exchange(msg);

        opts.ResolveOption("${header.region}", exchange).Should().Be("us-east");
    }

    [Fact]
    public void ResolveOption_MixedTemplate_ResolvesExpressionParts()
    {
        var opts = new TestEndpointOptions { Host = "any" };

        var msg = new Message();
        msg.Headers["env"] = "prod";
        msg.Headers["id"] = "42";
        var exchange = new Exchange(msg);

        opts.ResolveOption("server-${header.env}-node-${header.id}", exchange)
            .Should().Be("server-prod-node-42");
    }

    [Fact]
    public void ResolveOption_PropertyExpression_ResolvesFromProperties()
    {
        var opts = new TestEndpointOptions { Host = "any" };

        var exchange = new Exchange(new Message());
        exchange.Properties["token"] = "abc123";

        opts.ResolveOption("Bearer ${property.token}", exchange)
            .Should().Be("Bearer abc123");
    }

    [Fact]
    public void ResolveOption_CachesCompiledTemplate()
    {
        var opts = new TestEndpointOptions { Host = "any" };
        var template = "${header.x}";

        var msg1 = new Message();
        msg1.Headers["x"] = "first";
        var e1 = new Exchange(msg1);

        var msg2 = new Message();
        msg2.Headers["x"] = "second";
        var e2 = new Exchange(msg2);

        // Same template, different exchanges → different results (but compiled once)
        opts.ResolveOption(template, e1).Should().Be("first");
        opts.ResolveOption(template, e2).Should().Be("second");
    }

    [Fact]
    public void ResolveOption_DifferentTemplates_CachedIndependently()
    {
        var opts = new TestEndpointOptions { Host = "any" };

        var msg = new Message();
        msg.Headers["a"] = "alpha";
        msg.Headers["b"] = "beta";
        var exchange = new Exchange(msg);

        opts.ResolveOption("${header.a}", exchange).Should().Be("alpha");
        opts.ResolveOption("${header.b}", exchange).Should().Be("beta");
    }

    // --- BindDynamicValue with ${...} expressions ---

    [Fact]
    public void BindFromUri_DynamicValueWithExpression_CreatesDynamic()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["key"] = "${header.orderId}" });

        opts.Key.Should().NotBeNull();
        opts.Key!.Value.IsDynamic.Should().BeTrue();

        var msg = new Message();
        msg.Headers["orderId"] = "ORD-999";
        var exchange = new Exchange(msg);

        opts.Key!.Value.Resolve(exchange).Should().Be("ORD-999");
    }

    [Fact]
    public void BindFromUri_DynamicValueWithMixedExpression_CreatesDynamic()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["key"] = "prefix-${header.id}-suffix" });

        opts.Key.Should().NotBeNull();
        opts.Key!.Value.IsDynamic.Should().BeTrue();

        var msg = new Message();
        msg.Headers["id"] = "42";
        var exchange = new Exchange(msg);

        opts.Key!.Value.Resolve(exchange).Should().Be("prefix-42-suffix");
    }

    [Fact]
    public void BindFromUri_DynamicValueStaticString_CreatesStatic()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["key"] = "static-key" });

        opts.Key.Should().NotBeNull();
        opts.Key!.Value.IsDynamic.Should().BeFalse();
        opts.Key!.Value.Resolve(new Exchange()).Should().Be("static-key");
    }

    [Fact]
    public void BindFromUri_DynamicValueInt_ExpressionGoesToUnmapped()
    {
        // int properties can't hold ${...} expressions — ConvertValue fails → unmapped
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["ttl"] = "${header.ttl}" });

        // DynamicValue<int> with ${...} → BindDynamicValue detects expression, creates dynamic
        // Actually, StringExpression wrapped in DynamicValue<int> — it tries Evaluate<int>
        opts.Ttl.Should().NotBeNull();
        opts.Ttl!.Value.IsDynamic.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_IntPropertyWithExpression_GoesToUnmapped()
    {
        // Plain int property (not DynamicValue<int>) can't hold ${...}
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["port"] = "${header.port}" });

        // int.Parse("${header.port}") fails → goes to unmapped
        opts.Port.Should().Be(5672); // default unchanged
        opts.UnmappedParameters.Should().ContainKey("port");
    }

    // --- DynamicValue<T>.FromExpression(IExpression) ---

    [Fact]
    public void FromExpression_IExpression_ResolvesPerMessage()
    {
        var expr = new StringExpression("${header.key}");
        var dv = DynamicValue<string>.FromExpression(expr);

        dv.IsDynamic.Should().BeTrue();

        var msg = new Message();
        msg.Headers["key"] = "resolved-value";
        dv.Resolve(new Exchange(msg)).Should().Be("resolved-value");
    }

    [Fact]
    public void FromExpression_IExpression_NullThrows()
    {
        var act = () => DynamicValue<string>.FromExpression((IExpression)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromExpression_IExpression_DifferentMessages_DifferentResults()
    {
        var expr = new StringExpression("user-${header.userId}");
        var dv = DynamicValue<string>.FromExpression(expr);

        var msg1 = new Message();
        msg1.Headers["userId"] = "alice";

        var msg2 = new Message();
        msg2.Headers["userId"] = "bob";

        dv.Resolve(new Exchange(msg1)).Should().Be("user-alice");
        dv.Resolve(new Exchange(msg2)).Should().Be("user-bob");
    }

    // --- End-to-end: URI → BindFromUri → ResolveOption flow ---

    [Fact]
    public void EndToEnd_ExpressionInStringProperty_ResolvedViaResolveOption()
    {
        // Simulate: kafka:topic?host=${header.target}
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["host"] = "${header.target}" });

        // Host is a plain string property, stores "${header.target}" as-is
        opts.Host.Should().Be("${header.target}");

        // Producer calls ResolveOption to resolve per message
        var msg = new Message();
        msg.Headers["target"] = "prod-server-1";
        var exchange = new Exchange(msg);

        opts.ResolveOption(opts.Host, exchange).Should().Be("prod-server-1");
    }

    [Fact]
    public void EndToEnd_StaticStringProperty_PassesThroughResolveOption()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["host"] = "static-server" });

        opts.Host.Should().Be("static-server");

        // No ${...} → passes through without compilation
        opts.ResolveOption(opts.Host, new Exchange()).Should().Be("static-server");
    }

    [Fact]
    public void EndToEnd_DynamicValueProperty_ResolvedViaResolve()
    {
        // DynamicValue<string> with ${...} → dynamic resolve
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string> { ["key"] = "${header.partitionKey}" });

        var msg = new Message();
        msg.Headers["partitionKey"] = "region-eu";
        var exchange = new Exchange(msg);

        opts.Key!.Value.Resolve(exchange).Should().Be("region-eu");
    }

    [Fact]
    public void EndToEnd_MultipleExpressionProperties_IndependentResolution()
    {
        var opts = new TestEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "${header.target}",
            ["key"] = "${property.routingKey}"
        });

        var msg = new Message();
        msg.Headers["target"] = "broker-3";
        var exchange = new Exchange(msg);
        exchange.Properties["routingKey"] = "orders.eu";

        opts.ResolveOption(opts.Host, exchange).Should().Be("broker-3");
        opts.Key!.Value.Resolve(exchange).Should().Be("orders.eu");
    }
}
