using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// <see cref="DefaultHeaderFilterStrategy"/>: Apache Camel's <c>DefaultHeaderFilterStrategy</c> semantics. A list of
/// names, a pattern and prefixes per direction; <c>true</c> drops a header; <c>FilterOnMatch=false</c> turns the
/// configuration into a whitelist; null values, case and the two directions kept apart.
/// </summary>
public class DefaultHeaderFilterStrategyTests
{
    private static readonly Exchange Exchange = new(new Message());

    private static bool Out(DefaultHeaderFilterStrategy s, string name, object? value = null)
        => s.FilterOutgoing(name, value ?? "v", Exchange);

    private static bool In(DefaultHeaderFilterStrategy s, string name, object? value = null)
        => s.FilterIncoming(name, value ?? "v", Exchange);

    [Fact]
    public void Empty_DropsNothing()
    {
        var s = new DefaultHeaderFilterStrategy();

        Out(s, "X-Corr").Should().BeFalse();
        In(s, "X-Corr").Should().BeFalse();
    }

    [Fact]
    public void Blacklist_DropsWhatMatches_ByName_Pattern_Prefix()
    {
        var s = new DefaultHeaderFilterStrategy
        {
            OutFilter = ["Authorization"],
            OutFilterPattern = "X-Secret-.*",
            OutFilterStartsWith = ["redb"],
        };

        Out(s, "Authorization").Should().BeTrue();
        Out(s, "X-Secret-Key").Should().BeTrue();
        Out(s, "redbHttp.Method").Should().BeTrue();
        Out(s, "X-Corr").Should().BeFalse();
    }

    [Fact]
    public void Whitelist_KeepsOnlyWhatMatches()
    {
        var s = new DefaultHeaderFilterStrategy { OutFilter = ["Correlation-ID"], FilterOnMatch = false };

        Out(s, "Correlation-ID").Should().BeFalse("listed: kept");
        Out(s, "Authorization").Should().BeTrue("not listed: dropped");
        Out(s, "X-Corr").Should().BeTrue();
    }

    [Fact]
    public void Whitelist_WithNothingConfigured_DropsEverything()
    {
        var s = new DefaultHeaderFilterStrategy { FilterOnMatch = false };

        Out(s, "X-Corr").Should().BeTrue();
        In(s, "X-Corr").Should().BeTrue();
    }

    [Fact]
    public void TheDirections_AreSeparate()
    {
        var s = new DefaultHeaderFilterStrategy { OutFilter = ["X-Out"], InFilter = ["X-In"] };

        Out(s, "X-Out").Should().BeTrue();
        Out(s, "X-In").Should().BeFalse("the incoming list does not filter outgoing headers");
        In(s, "X-In").Should().BeTrue();
        In(s, "X-Out").Should().BeFalse();
    }

    [Fact]
    public void ThePattern_MatchesTheWholeName()
    {
        var s = new DefaultHeaderFilterStrategy { OutFilterPattern = "X-[A-Z]+" };

        Out(s, "X-ABC").Should().BeTrue();
        Out(s, "X-ABC-more").Should().BeFalse("a pattern is matched against the whole name, as in Camel");
        Out(s, "prefix-X-ABC").Should().BeFalse();
    }

    [Fact]
    public void ANullValue_IsDropped_UnlessAllowed()
    {
        var s = new DefaultHeaderFilterStrategy();
        s.FilterOutgoing("X-Null", null, Exchange).Should().BeTrue();

        s.AllowNullValues = true;
        s.FilterOutgoing("X-Null", null, Exchange).Should().BeFalse();
    }

    [Fact]
    public void Names_IgnoreCase_ByDefault_AsInCamel()
    {
        var s = new DefaultHeaderFilterStrategy { OutFilter = ["Authorization"] };
        Out(s, "authorization").Should().BeTrue();
        Out(s, "AUTHORIZATION").Should().BeTrue();

        s.CaseInsensitive = false;
        Out(s, "authorization").Should().BeFalse("exact when case-insensitivity is off");
    }

    [Fact]
    public void LowerCase_DoesNotApplyToThePattern()
    {
        var s = new DefaultHeaderFilterStrategy { OutFilterPattern = "x-[a-z]+", LowerCase = true };

        Out(s, "X-Trace").Should().BeFalse("the pattern is matched against the name as it is, as in Camel");
        Out(s, "x-trace").Should().BeTrue();
    }

    [Fact]
    public void LowerCase_AlsoMatchesTheLowerCasedName()
    {
        var s = new DefaultHeaderFilterStrategy { OutFilterStartsWith = ["x-"], LowerCase = true };

        Out(s, "X-Trace").Should().BeTrue("'x-trace' starts with 'x-'");
    }

    [Fact]
    public void ThePattern_FollowsTheProperty()
    {
        var s = new DefaultHeaderFilterStrategy { OutFilterPattern = "A" };
        Out(s, "A").Should().BeTrue();

        s.OutFilterPattern = "B";
        Out(s, "A").Should().BeFalse("a changed pattern is compiled again");
        Out(s, "B").Should().BeTrue();
    }

    [Fact]
    public void Resolve_FindsTheRegisteredStrategy_AndRefusesAMissingOne()
    {
        var ctx = new RouteContext();
        var strategy = new DefaultHeaderFilterStrategy();
        ctx.AddToRegistry("onlyCorrelation", strategy);

        HeaderFilterStrategies.Resolve(ctx, "#onlyCorrelation", "http://x").Should().BeSameAs(strategy);
        HeaderFilterStrategies.Resolve(ctx, "onlyCorrelation", "http://x").Should().BeSameAs(strategy);
        HeaderFilterStrategies.Resolve(ctx, null, "http://x").Should().BeNull("no option: the transport's default");

        var missing = () => HeaderFilterStrategies.Resolve(ctx, "#nope", "http://x");
        missing.Should().Throw<InvalidOperationException>().WithMessage("*http://x*'#nope'*");
        ctx.AddToRegistry("notAStrategy", "text");
        var wrongType = () => HeaderFilterStrategies.Resolve(ctx, "#notAStrategy", "http://x");
        wrongType.Should().Throw<InvalidOperationException>();
    }
}
