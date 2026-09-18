using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Llm.Extensions;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// Claims enforcement (wave 2). A tool that declares <c>.RequireClaim(...)</c> may only fire when the
/// run's principal provably holds the claim; without a verifiable principal it is denied, and the
/// model is told the error code only.
/// <para>
/// The realistic runtime shape is exercised with the shipped default source, which reads the caller
/// from the exchange properties (never a header). A tool that declares claims with <b>no</b> source
/// registered does not even build — see <see cref="RequireClaim_WithoutClaimsSource_FailsRouteBuild"/>.
/// </para>
/// </summary>
[Trait("Category", "Governance")]
public sealed class RequiredClaimsTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string Claim = "orders:read";

    /// <summary>An authenticated caller carrying each of <paramref name="scopeValues"/> as its own <c>scope</c> claim.</summary>
    private static ClaimsPrincipal Principal(params string[] scopeValues)
        => PrincipalWithClaimType("scope", scopeValues);

    /// <summary>An authenticated caller whose values arrive under <paramref name="claimType"/>.</summary>
    private static ClaimsPrincipal PrincipalWithClaimType(string claimType, params string[] values)
        => new(new ClaimsIdentity(values.Select(v => new Claim(claimType, v)), authenticationType: "test"));

    /// <summary>
    /// Runs a claim-required tool through the shipped default source, with <paramref name="principal"/>
    /// placed on the exchange the way a transport would (a property, before the route runs).
    /// </summary>
    private static async Task<(EchoToolRoute Tool, ToolInvocationSpy Spy, IExchange Exchange)> RunWithPrincipalAsync(
        ClaimsPrincipal? principal)
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", """{"q":"x"}""", "tu_1")
            .EnqueueText("done");
        var spy = new ToolInvocationSpy();

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        await using var host = LiveLlmHost.Build(spy, claimsSource: new ExchangePrincipalClaimsSource())
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        var exchange = await host.SendAsync("direct:agent", "Call the tool.", configure: ex =>
        {
            if (principal is not null) ExchangePrincipal.Set(ex, principal);
        });

        return (tool, spy, exchange);
    }

    [Fact]
    public async Task RequiredClaimsTool_WithoutPrincipal_IsDenied()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", """{"q":"x"}""", "tu_1")
            .EnqueueText("done");
        var spy = new ToolInvocationSpy();

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        await using var host = LiveLlmHost.Build(spy, claimsSource: new DelegateToolClaimsSource(_ => null))
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.");

        tool.CapturedInputs.Should().BeEmpty(
            "a tool whose claims cannot be verified must never be invoked (fail closed)");
        spy.ForTool("lookup").Should().NotBeEmpty("the denial is reported to observers")
            .And.OnlyContain(i => i.Skipped && i.SkipReason != null && i.SkipReason.StartsWith("claims_missing"));
    }

    [Fact]
    public async Task RequiredClaimsTool_WithUserHeaderButNoPrincipal_IsDenied()
    {
        // The header is the *audit identity* fallback, not an authorization source: a client-supplied
        // string must not be enough to satisfy a declared claim.
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", """{"q":"x"}""", "tu_1")
            .EnqueueText("done");

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        await using var host = LiveLlmHost.Build(claimsSource: new DelegateToolClaimsSource(_ => null))
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.",
            new Dictionary<string, object?> { [LlmHeaders.UserId] = "user-1" });

        tool.CapturedInputs.Should().BeEmpty(
            "an unverified client-supplied identity must not satisfy RequiredClaims");
    }

    [Fact]
    public async Task RequiredClaimsTool_WithAllClaims_IsInvoked()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", """{"q":"x"}""", "tu_1")
            .EnqueueText("done");
        var spy = new ToolInvocationSpy();

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        await using var host = LiveLlmHost.Build(spy,
                claimsSource: new DelegateToolClaimsSource(_ => [Claim, "orders:write"]))
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.");

        tool.CapturedInputs.Should().HaveCount(1, "a principal holding the claim runs the tool");
        spy.ForTool("lookup").Should().Contain(i => !i.Skipped, "and the invocation is not a skip");
    }

    [Fact]
    public async Task RequireClaim_WithoutClaimsSource_FailsRouteBuild()
    {
        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: [Claim]);

        // No claims source on purpose: the tool could never fire, so the route must not build.
        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub" });

        var thrown = await Record.ExceptionAsync(() => host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done")));

        thrown.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("IToolClaimsSource",
                "the message must name the missing registration so the fix is obvious");
    }

    /// <summary>
    /// The positive path through the shipped default source: the caller proved itself to a transport,
    /// the principal sits on the exchange, and the required scope is there.
    /// </summary>
    [Fact]
    public async Task RequiredClaimsTool_WithVerifiedPrincipalScope_IsInvoked()
    {
        var (tool, spy, _) = await RunWithPrincipalAsync(Principal(Claim));

        tool.CapturedInputs.Should().HaveCount(1, "the caller holds the required scope");
        spy.ForTool("lookup").Should().Contain(i => !i.Skipped);
    }

    /// <summary>Microsoft Entra ID spells the same vocabulary under <c>scp</c>; the default source reads both.</summary>
    [Fact]
    public async Task RequiredClaimsTool_ScpClaimType_IsHonoured()
    {
        var (tool, _, _) = await RunWithPrincipalAsync(PrincipalWithClaimType("scp", Claim));

        tool.CapturedInputs.Should().HaveCount(1);
    }

    /// <summary>
    /// Real issuers split one logical set into several claims and pad the values; neither may turn a
    /// satisfied requirement into a denial.
    /// </summary>
    [Fact]
    public async Task RequiredClaimsTool_PaddedAndRepeatedScopes_AreNormalized()
    {
        var (tool, _, _) = await RunWithPrincipalAsync(
            PrincipalWithClaimType("scope", $"{Claim}   orders:write ", Claim, "  orders:write  "));

        tool.CapturedInputs.Should().HaveCount(1, "padding and repetition are formatting, not a different claim");
    }

    /// <summary>A verified caller who simply does not hold the scope is denied — with the audit reason.</summary>
    [Fact]
    public async Task RequiredClaimsTool_PrincipalWithoutTheScope_IsDenied()
    {
        var (tool, spy, _) = await RunWithPrincipalAsync(Principal("orders:write"));

        tool.CapturedInputs.Should().BeEmpty("holding another scope is not holding this one");
        spy.ForTool("lookup").Should().OnlyContain(i => i.Skipped
            && i.SkipReason != null && i.SkipReason.StartsWith(ToolSkipReasons.ClaimsMissing));
    }

    /// <summary>
    /// An identity that is not authenticated is not a principal: a transport that passes through an
    /// anonymous placeholder must not be able to satisfy a requirement.
    /// </summary>
    [Fact]
    public async Task RequiredClaimsTool_UnauthenticatedPrincipal_IsDenied()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", Claim)]));

        var (tool, _, _) = await RunWithPrincipalAsync(anonymous);

        tool.CapturedInputs.Should().BeEmpty(
            "claims on an unauthenticated identity are a claim about nothing");
    }

    /// <summary>
    /// The caller reaches the exchange the tool itself runs on — the identity survives the linked child
    /// the agent dispatches into, which is what makes a claims check meaningful inside a tool route.
    /// </summary>
    [Fact]
    public async Task Principal_ReachesTheToolExchange()
    {
        var principal = Principal(Claim);

        var (tool, _, _) = await RunWithPrincipalAsync(principal);

        tool.CapturedProperties.Should().Contain(
            p => p.ContainsKey(ExchangePrincipal.PropertyKey)
                 && ReferenceEquals(p[ExchangePrincipal.PropertyKey], principal),
            "a tool route reads the caller from its own exchange, not from a translation of the request");
    }

    /// <summary>
    /// Scope values are space-delimited (RFC 6749 §3.3); a comma is a legal character <b>inside</b> a
    /// scope token. Treating it as a separator would turn one granted scope — <c>orders:read,orders:write</c>
    /// — into two grants, which is the one direction a security check must never fail in.
    /// </summary>
    [Fact]
    public async Task RequiredClaimsTool_CommaIsNotAScopeSeparator()
    {
        var (tool, spy, _) = await RunWithPrincipalAsync(Principal($"{Claim},orders:write"));

        tool.CapturedInputs.Should().BeEmpty(
            "the value is a single scope token containing a comma, not two scopes");
        spy.ForTool("lookup").Should().OnlyContain(i => i.Skipped);
    }

    /// <summary>
    /// The default source on its own: which claim types it reads, and the two states it must keep apart
    /// — "no principal" and "a principal that holds nothing".
    /// </summary>
    [Fact]
    public void ExchangePrincipalClaimsSource_ReadsScopeClaimTypes_AndRefusesAnonymous()
    {
        var source = new ExchangePrincipalClaimsSource();
        var exchange = Exchange.Create(new Message("x"), scopeFactory: null);

        source.GetClaims(exchange).Should().BeNull("an exchange with no principal proves nothing");

        ExchangePrincipal.Set(exchange, PrincipalWithClaimType(
            "http://schemas.microsoft.com/identity/claims/scope", $"{Claim} orders:write"));
        source.GetClaims(exchange).Should().BeEquivalentTo([Claim, "orders:write"],
            "the Entra ID v1 scope claim type carries the same vocabulary");

        ExchangePrincipal.Set(exchange,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", Claim)])));
        source.GetClaims(exchange).Should().BeNull(
            "an identity that is not authenticated is not a principal, so an anonymous placeholder cannot satisfy a requirement");

        ExchangePrincipal.Set(exchange, Principal());
        source.GetClaims(exchange).Should().BeEmpty(
            "a principal holding nothing differs from no principal — both deny, but only one is diagnosable");
    }

    /// <summary>
    /// Claims work out of the box: the shipped container resolves the caller-based source, so a tool
    /// route can declare a requirement without the host wiring anything.
    /// </summary>
    [Fact]
    public void AddRedbRouteLlm_RegistersPrincipalClaimsSourceByDefault()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteLlm();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IToolClaimsSource>().Should().BeOfType<ExchangePrincipalClaimsSource>(
            "the caller's principal is the default answer to \"what may this run do\"");
    }

    /// <summary>A host with its own vocabulary keeps it: the default registration must not win over it.</summary>
    [Fact]
    public void HostRegisteredClaimsSource_WinsOverTheDefault()
    {
        var services = new ServiceCollection();
        var mine = new DelegateToolClaimsSource(_ => ["host:everything"]);
        services.AddSingleton<IToolClaimsSource>(mine);
        services.AddRedbRouteLlm();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IToolClaimsSource>().Should().BeSameAs(mine);
    }
}
