namespace redb.Route.Tests.Llm;

/// <summary>
/// Unit tests for <see cref="ToolHeaderPolicy"/> — the default-deny allowlist that
/// decides what a tool route sees of the agent exchange.
/// </summary>
public sealed class ToolHeaderPolicyTests
{
    private static AgentRequest Request(
        IDictionary<string, object?>? parentHeaders = null,
        string? userId = null,
        IReadOnlyDictionary<string, string>? auditTags = null,
        IReadOnlyList<string>? propagate = null)
    {
        var msg = new Message("body");
        if (parentHeaders is not null)
            foreach (var kv in parentHeaders) msg.Headers[kv.Key] = kv.Value;

        return new AgentRequest
        {
            Factory = new LlmConnectionFactory { Name = "f", Provider = "stub", ModelId = "m" },
            Exchange = new Exchange(msg),
            UserContent = [new LlmTextBlock("hi")],
            UserId = userId,
            AuditTags = auditTags,
            PropagateToolHeaders = propagate
        };
    }

    private static Dictionary<string, object?> Apply(AgentRequest request)
    {
        var target = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        ToolHeaderPolicy.Apply(request, target);
        return target;
    }

    [Fact]
    public void Defaults_CarryConversationAndCorrelationIds()
    {
        var target = Apply(Request(new Dictionary<string, object?>
        {
            [LlmHeaders.ConversationId] = "conv-1",
            ["X-Correlation-Id"] = "corr-1",
            ["CorrelationId"] = "corr-2"
        }));

        target[LlmHeaders.ConversationId].Should().Be("conv-1");
        target["X-Correlation-Id"].Should().Be("corr-1");
        target["CorrelationId"].Should().Be("corr-2");
    }

    [Fact]
    public void ArbitraryInboundHeader_IsNotPropagated()
    {
        // The whole point of default-deny: an HTTP consumer's headers must not
        // ride along into every tool just because the tool shares the host.
        var target = Apply(Request(new Dictionary<string, object?>
        {
            ["Authorization"] = "Bearer secret",
            ["Cookie"] = "session=abc",
            ["x-tenant-id"] = "acme"
        }));

        target.Should().NotContainKey("Authorization");
        target.Should().NotContainKey("Cookie");
        target.Should().NotContainKey("x-tenant-id");
    }

    [Fact]
    public void ResolvedUserId_WinsOverHeader()
    {
        // `?user=${header.X-User}` resolves before the run; copying the raw
        // llm.user.id header would have dropped exactly this case.
        var target = Apply(Request(
            parentHeaders: new Dictionary<string, object?> { [LlmHeaders.UserId] = "from-header" },
            userId: "resolved-alice"));

        target[LlmHeaders.UserId].Should().Be("resolved-alice");
    }

    [Fact]
    public void UserIdHeader_IsFallbackWhenNothingResolved()
    {
        // Engine driven without LlmProducer (inline .Llm(), eval runner) still propagates.
        var target = Apply(Request(
            parentHeaders: new Dictionary<string, object?> { [LlmHeaders.UserId] = "from-header" }));

        target[LlmHeaders.UserId].Should().Be("from-header");
    }

    [Fact]
    public void ResolvedAuditTags_BecomePrefixedHeaders()
    {
        var target = Apply(Request(auditTags: new Dictionary<string, string>
        {
            ["tier"] = "gold",
            ["bucket"] = "A"
        }));

        target[LlmHeaders.AuditTagPrefix + "tier"].Should().Be("gold");
        target[LlmHeaders.AuditTagPrefix + "bucket"].Should().Be("A");
    }

    [Fact]
    public void AuditHeaders_AreFallbackWhenNoResolvedTags()
    {
        var target = Apply(Request(new Dictionary<string, object?>
        {
            [LlmHeaders.AuditTagPrefix + "region"] = "eu-west",
            ["llm.unrelated"] = "nope"
        }));

        target[LlmHeaders.AuditTagPrefix + "region"].Should().Be("eu-west");
        target.Should().NotContainKey("llm.unrelated");
    }

    [Fact]
    public void OptIn_CopiesExactNames()
    {
        var target = Apply(Request(
            parentHeaders: new Dictionary<string, object?>
            {
                ["x-tenant-id"] = "acme",
                ["Authorization"] = "Bearer secret"
            },
            propagate: ["x-tenant-id"]));

        target["x-tenant-id"].Should().Be("acme");
        target.Should().NotContainKey("Authorization");
    }

    [Fact]
    public void OptIn_SupportsTrailingStarAsPrefix()
    {
        var target = Apply(Request(
            parentHeaders: new Dictionary<string, object?>
            {
                ["x-app-locale"] = "ru-RU",
                ["x-app-build"] = "42",
                ["x-other"] = "no"
            },
            propagate: ["x-app-*"]));

        target["x-app-locale"].Should().Be("ru-RU");
        target["x-app-build"].Should().Be("42");
        target.Should().NotContainKey("x-other");
    }

    [Fact]
    public void NullHeaderValues_AreSkipped()
    {
        var target = Apply(Request(
            parentHeaders: new Dictionary<string, object?> { ["x-tenant-id"] = null },
            propagate: ["x-tenant-id"]));

        target.Should().NotContainKey("x-tenant-id");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public void ParseCsv_ReturnsNullForEmptyInput(string? csv)
        => ToolHeaderPolicy.ParseCsv(csv).Should().BeNull();

    [Fact]
    public void ParseCsv_TrimsAndUrlDecodes()
    {
        var parsed = ToolHeaderPolicy.ParseCsv("x-tenant-id, x-app-* ,accept%2Dlanguage");

        parsed.Should().BeEquivalentTo(["x-tenant-id", "x-app-*", "accept-language"]);
    }
}
