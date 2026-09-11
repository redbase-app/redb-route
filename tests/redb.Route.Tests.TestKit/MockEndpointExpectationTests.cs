using System.Diagnostics;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.TestKit;

/// <summary>Rich mock: every expectation has a satisfied and an unsatisfied case, and the failure text is asserted.</summary>
public class MockEndpointExpectationTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    private static RouteContext Context(string inUri = "direct://m-in", string mockUri = "mock://m")
        => new RouteContext().AddRoutes(b => b.From(inUri).To(mockUri));

    [Fact]
    public async Task ExpectMessageCount_Satisfied_And_Unsatisfied()
    {
        await using var ctx = Context();
        await ctx.Start();
        var mock = ctx.Mock("mock://m").ExpectMessageCount(2);

        await ctx.SendBody("direct://m-in", "a");
        var act = () => mock.AssertIsSatisfiedAsync(Short);
        var ex = await act.Should().ThrowAsync<MockAssertionException>();
        ex.Which.Message.Should().Contain("mock://m Received message count. Expected: 2 but was: 1")
            .And.Contain("received 1 message(s):")
            .And.Contain("1: \"a\"");

        await ctx.SendBody("direct://m-in", "b");
        await mock.AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task ExpectMinimumMessageCount()
    {
        await using var ctx = Context();
        await ctx.Start();
        var mock = ctx.Mock("mock://m").ExpectMinimumMessageCount(2);

        await ctx.SendBody("direct://m-in", "a");
        var act = () => mock.AssertIsSatisfiedAsync(Short);
        (await act.Should().ThrowAsync<MockAssertionException>()).Which.Message.Should().Contain("Expected at least: 2 but was: 1");

        await ctx.SendBody("direct://m-in", "b");
        await ctx.SendBody("direct://m-in", "c");
        await mock.AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task ExpectBodies_InOrder()
    {
        await using var ctx = Context();
        await ctx.Start();
        var mock = ctx.Mock("mock://m").ExpectBodies("a", "b");

        await ctx.SendBody("direct://m-in", "a");
        await ctx.SendBody("direct://m-in", "x");
        var act = () => mock.AssertIsSatisfiedAsync(Short);
        (await act.Should().ThrowAsync<MockAssertionException>()).Which.Message
            .Should().Contain("Body of message 2. Expected: \"b\" but was: \"x\"");

        mock.Reset();
        mock.ExpectBodies("a", "b");
        await ctx.SendBody("direct://m-in", "a");
        await ctx.SendBody("direct://m-in", "b");
        await mock.AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task ExpectBodiesInAnyOrder()
    {
        await using var ctx = Context();
        await ctx.Start();
        var mock = ctx.Mock("mock://m").ExpectBodiesInAnyOrder("a", "b");

        await ctx.SendBody("direct://m-in", "b");
        await ctx.SendBody("direct://m-in", "a");
        await mock.AssertIsSatisfiedAsync(Wait);

        mock.Reset();
        mock.ExpectBodiesInAnyOrder("a", "b");
        await ctx.SendBody("direct://m-in", "a");
        await ctx.SendBody("direct://m-in", "c");
        var act = () => mock.AssertIsSatisfiedAsync(Short);
        (await act.Should().ThrowAsync<MockAssertionException>()).Which.Message
            .Should().Contain("Body \"b\" expected in any order but was not received");
    }

    [Fact]
    public async Task ExpectBodies_ComparesNumbersAcrossClrTypes()
    {
        await using var ctx = Context();
        await ctx.Start();

        await ctx.SendBody("direct://m-in", 42);

        await ctx.Mock("mock://m").ExpectBodies(42L).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task ExpectHeader_OnEveryMessage_And_ExpectHeaderReceived_OnAny()
    {
        await using var ctx = Context();
        await ctx.Start();
        var mock = ctx.Mock("mock://m");

        await ctx.SendBodyAndHeader("direct://m-in", "a", "priority", "high");
        await ctx.SendBodyAndHeader("direct://m-in", "b", "priority", "low");

        mock.ExpectHeaderReceived("priority", "high");
        await mock.AssertIsSatisfiedAsync(Wait);

        mock.ExpectHeader("priority", "high");
        var act = () => mock.AssertIsSatisfiedAsync(Short);
        (await act.Should().ThrowAsync<MockAssertionException>()).Which.Message
            .Should().Contain("Header 'priority' on message 2. Expected: \"high\" but was: \"low\"");
    }

    [Fact]
    public async Task ExpectHeader_ReportsAbsentHeader()
    {
        await using var ctx = Context();
        await ctx.Start();
        var mock = ctx.Mock("mock://m").ExpectHeader("missing", 1);

        await ctx.SendBody("direct://m-in", "a");

        var act = () => mock.AssertIsSatisfiedAsync(Short);
        (await act.Should().ThrowAsync<MockAssertionException>()).Which.Message.Should().Contain("but was: <absent>");
    }

    [Fact]
    public async Task ExpectProperty()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://p-in").SetProperty("tenant", "acme").To("mock://p"));
        await ctx.Start();

        await ctx.SendBody("direct://p-in", "a");

        await ctx.Mock("mock://p").ExpectProperty("tenant", "acme").AssertIsSatisfiedAsync(Wait);
        var act = () => ctx.Mock("mock://p").ExpectProperty("tenant", "other").AssertIsSatisfiedAsync(Short);
        (await act.Should().ThrowAsync<MockAssertionException>()).Which.Message.Should().Contain("Property 'tenant' on message 1");
    }

    [Fact]
    public async Task Expect_ConditionString_UsesTheRouteLanguage()
    {
        await using var ctx = Context();
        await ctx.Start();
        var mock = ctx.Mock("mock://m").Expect("header.priority == 'high'");

        await ctx.SendBodyAndHeader("direct://m-in", "a", "priority", "high");
        await mock.AssertIsSatisfiedAsync(Wait);

        await ctx.SendBodyAndHeader("direct://m-in", "b", "priority", "low");
        var act = () => mock.AssertIsSatisfiedAsync(Short);
        (await act.Should().ThrowAsync<MockAssertionException>()).Which.Message
            .Should().Contain("Predicate 'header.priority == 'high'' failed on message 2");
    }

    [Fact]
    public async Task Expect_Lambda()
    {
        await using var ctx = Context();
        await ctx.Start();

        await ctx.SendBody("direct://m-in", "abc");

        await ctx.Mock("mock://m").Expect(e => e.In.Body is string { Length: 3 }, "length 3").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task UriOption_ExpectedMessageCount_IsAnExpectation()
    {
        await using var ctx = Context(mockUri: "mock://opt?expectedMessageCount=2");
        await ctx.Start();
        var mock = ctx.Mock("mock://opt?expectedMessageCount=2");

        await ctx.SendBody("direct://m-in", "a");
        var act = () => mock.AssertIsSatisfiedAsync(Short);
        (await act.Should().ThrowAsync<MockAssertionException>()).Which.Message.Should().Contain("Expected: 2 but was: 1");

        await ctx.SendBody("direct://m-in", "b");
        await mock.AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task AssertIsNotSatisfied()
    {
        await using var ctx = Context();
        await ctx.Start();
        var mock = ctx.Mock("mock://m").ExpectMessageCount(2);

        await ctx.SendBody("direct://m-in", "a");
        await mock.AssertIsNotSatisfiedAsync(Short);

        await ctx.SendBody("direct://m-in", "b");
        var act = () => mock.AssertIsNotSatisfiedAsync(Short);
        await act.Should().ThrowAsync<MockAssertionException>().WithMessage("*were satisfied but were expected not to be*");
    }

    [Fact]
    public async Task Expectations_SeeTheMessageAsItArrived_NotAsLaterStepsMutatedIt()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://cap-in").To("mock://cap").SetBody("mutated"));
        await ctx.Start();
        var mock = ctx.Mock("mock://cap").ExpectBodies("orig");

        await ctx.SendBody("direct://cap-in", "orig");

        await mock.AssertIsSatisfiedAsync(Wait);
        mock.ReceivedExchanges[0].In.Body.Should().Be("mutated", "ReceivedExchanges keeps the live exchange");
    }

    [Fact]
    public async Task Whenever_SetBody_RepliesToEnrich()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://e-in")
                .Enrich("mock://svc", (original, resource) => { original.In.Body = resource.In.Body; return original; })
                .To("mock://e-out"));
        await ctx.Start();
        ctx.Mock("mock://svc").Whenever(1).SetBody("reply-1");
        ctx.Mock("mock://svc").Whenever(2).SetBody("reply-2");

        await ctx.SendBody("direct://e-in", "q");
        await ctx.SendBody("direct://e-in", "q");

        await ctx.Mock("mock://e-out").ExpectBodies("reply-1", "reply-2").AssertIsSatisfiedAsync(Wait);
        ctx.Mock("mock://svc").ReceivedCount.Should().Be(2);
    }

    [Fact]
    public async Task Whenever_Throw_IsCountedAndPropagates()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://t-in")
                .TryCatch()
                    .To("mock://flaky")
                .Catch<InvalidOperationException>()
                    .To("mock://caught")
                .EndTryCatch());
        await ctx.Start();
        ctx.Mock("mock://flaky").Whenever(2).Throw<InvalidOperationException>("second call fails");

        await ctx.SendBody("direct://t-in", "1");
        await ctx.SendBody("direct://t-in", "2");

        await ctx.Mock("mock://flaky").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);
        await ctx.Mock("mock://caught").ExpectBodies("2").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task WheneverAny_Delay_SlowsEveryMessage()
    {
        await using var ctx = Context();
        await ctx.Start();
        ctx.Mock("mock://m").WheneverAny().Delay(TimeSpan.FromMilliseconds(120));

        var clock = Stopwatch.StartNew();
        await ctx.SendBody("direct://m-in", "a");

        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90));
    }

    [Fact]
    public async Task Reset_ClearsReceivedAndExpectations()
    {
        await using var ctx = Context();
        await ctx.Start();
        var mock = ctx.Mock("mock://m").ExpectMessageCount(5);
        await ctx.SendBody("direct://m-in", "a");

        mock.Reset();

        mock.ReceivedCount.Should().Be(0);
        (await mock.IsSatisfiedAsync()).Should().BeTrue("no expectations remain");
    }
}
