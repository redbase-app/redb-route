using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Expressions;
using redb.Route.TestKit;

namespace redb.Route.Tests.Eip;

/// <summary>SetHeaders, RemoveHeaders(pattern), RemoveProperties(pattern), Sort.</summary>
public class RouteSugarTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task SetHeaders_Constant_Expression_And_Func_InOneStep()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://sh")
                .SetHeaders(
                    ("a", 1),
                    ("b", new StringExpression("header.a + 1")),
                    ("c", (Func<IExchange, object?>)(e => $"{e.In.Body}!")))
                .To("mock://sh"));
        await ctx.Start();

        await ctx.SendBody("direct://sh", "x");

        await ctx.Mock("mock://sh").ExpectHeader("a", 1).ExpectHeader("b", 2).ExpectHeader("c", "x!").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task SetProperties_Constant_Expression_And_Func_InOneStep_InOrder()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://sp")
                .SetProperties(
                    ("a", 1),
                    ("b", new StringExpression("property.a + 1")),
                    ("c", (Func<IExchange, object?>)(e => $"{e.In.Body}!")))
                .To("mock://sp"));
        await ctx.Start();

        await ctx.SendBody("direct://sp", "x");

        var properties = ctx.Mock("mock://sp").ReceivedExchanges[0].Properties;
        properties["a"].Should().Be(1);
        properties["b"].Should().Be(2, "a later property reads an earlier one of the same step");
        properties["c"].Should().Be("x!");
    }

    [Fact]
    public void SetProperties_WithoutProperties_IsRejected()
    {
        var act = () => new SetPropertiesDefinition([]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task RemoveHeaders_ByMask_WithExceptions()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://rh").RemoveHeaders("X-Internal-*", "X-Internal-Keep").To("mock://rh"));
        await ctx.Start();

        await ctx.SendBodyAndHeaders("direct://rh", "x", new Dictionary<string, object?>
        {
            ["X-Internal-A"] = 1, ["X-Internal-B"] = 2, ["X-Internal-Keep"] = 3, ["Public"] = 4,
        });

        var headers = ctx.Mock("mock://rh").ReceivedExchanges[0].In.Headers;
        headers.Keys.Should().BeEquivalentTo("X-Internal-Keep", "Public");
    }

    [Fact]
    public async Task RemoveProperties_ByRegex()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://rp")
                .SetProperty("tmp.a", 1).SetProperty("tmp.b", 2).SetProperty("keep", 3)
                .RemoveProperties("regex:^tmp\\..*")
                .To("mock://rp"));
        await ctx.Start();

        await ctx.SendBody("direct://rp", "x");

        var props = ctx.Mock("mock://rp").ReceivedExchanges[0].Properties;
        props.Should().ContainKey("keep").And.NotContainKey("tmp.a").And.NotContainKey("tmp.b");
    }

    [Fact]
    public async Task Sort_StringForm_ByKeyExpression_ElementIsBody()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://sort").Sort("body", "body.priority").To("mock://sorted"));
        await ctx.Start();

        await ctx.SendBody("direct://sort", new List<object?>
        {
            new { name = "c", priority = 3 }, new { name = "a", priority = 1 }, new { name = "b", priority = 2 },
        });

        var sorted = (List<object?>)ctx.Mock("mock://sorted").ReceivedExchanges[0].In.Body!;
        sorted.Select(o => (string)o!.GetType().GetProperty("name")!.GetValue(o)!).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Sort_Generic_Descending_ProducesTypedList()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://sort-g").Sort<int>(e => (IEnumerable<int>)e.In.Body!, descending: true).To("mock://sorted-g"));
        await ctx.Start();

        await ctx.SendBody("direct://sort-g", new[] { 2, 3, 1 });

        ctx.Mock("mock://sorted-g").ReceivedExchanges[0].In.Body.Should().BeOfType<List<int>>().Which.Should().Equal(3, 2, 1);
    }

    [Fact]
    public async Task Sort_MixedNumbersAndNulls()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://sort-m").Sort("body").To("mock://sorted-m"));
        await ctx.Start();

        await ctx.SendBody("direct://sort-m", new List<object?> { 3, null, 1.5m, 2L });

        ((List<object?>)ctx.Mock("mock://sorted-m").ReceivedExchanges[0].In.Body!).Should().Equal(null, 1.5m, 2L, 3);
    }

    [Fact]
    public async Task Sort_OnAString_Fails()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://sort-s").Sort("body"));
        await ctx.Start();

        var act = () => ctx.SendBody("direct://sort-s", "not a list");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Sort:*");
    }
}
