using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Dsl;

/// <summary>
/// End-to-end coverage of the single truthiness rule on live routes: real RouteContext, real
/// producers, real messages. Unit-level coverage lives in the characterisation snapshot; these
/// tests prove the rule reaches the wire — a zero header actually drops a message, a "yes"
/// header actually routes a branch.
/// </summary>
[Collection("ExpressionResolver")]
public class TruthinessEndToEndTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<IExchange> Send(string uri, object? headerValue)
    {
        var exchange = new Exchange(new Message("payload"));
        if (headerValue is not null)
            exchange.In.Headers["v"] = headerValue;
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        return exchange;
    }

    // ── Filter over a bare accessor ───────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData(0.0, false)]
    [InlineData(2.5, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("no", false)]
    [InlineData("yes", true)]
    [InlineData("off", false)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("anything", true)]
    public async Task Filter_BareAccessor_FollowsTheOneTruthinessRule(object value, bool shouldPass)
    {
        var passed = false;
        var uri = "direct://truth-" + Guid.NewGuid().ToString("N");
        _context.AddRoutes(r => r.From(uri).Filter("header.v").Process(_ => passed = true));
        await _context.Start();

        await Send(uri, value);

        passed.Should().Be(shouldPass, $"header value {value} ({value.GetType().Name}) reads as {shouldPass}");
    }

    [Fact]
    public async Task Filter_MissingHeader_IsFalse()
    {
        var passed = false;
        _context.AddRoutes(r => r.From("direct://truth-missing").Filter("header.v").Process(_ => passed = true));
        await _context.Start();

        await Send("direct://truth-missing", null);

        passed.Should().BeFalse();
    }

    // ── The same rule through a template placeholder ──────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(7, true)]
    [InlineData("no", false)]
    [InlineData("anything", true)]
    public async Task Filter_WholeStringPlaceholder_AgreesWithTheBareAccessor(object value, bool shouldPass)
    {
        var passed = false;
        var uri = "direct://truth-tpl-" + Guid.NewGuid().ToString("N");
        _context.AddRoutes(r => r.From(uri).Filter("${header.v}").Process(_ => passed = true));
        await _context.Start();

        await Send(uri, value);

        passed.Should().Be(shouldPass);
    }

    // ── The same rule inside the expression language ──────────────────────────

    [Theory]
    [InlineData("logical(header.v)")]
    [InlineData("header.v AND true")]
    [InlineData("NOT (NOT header.v)")]
    public async Task Filter_WordLogicAndLogicalFunction_AgreeWithTheBareAccessor(string condition)
    {
        var zeroPassed = false;
        var sevenPassed = false;
        var uriZero = "direct://truth-z-" + condition.GetHashCode().ToString("x");
        var uriSeven = "direct://truth-s-" + condition.GetHashCode().ToString("x");
        _context.AddRoutes(r =>
        {
            r.From(uriZero).Filter(condition).Process(_ => zeroPassed = true);
            r.From(uriSeven).Filter(condition).Process(_ => sevenPassed = true);
        });
        await _context.Start();

        await Send(uriZero, 0);
        await Send(uriSeven, 7);

        zeroPassed.Should().BeFalse("zero is false everywhere");
        sevenPassed.Should().BeTrue("a non-zero number is true everywhere");
    }

    // ── Choice takes the branch the rule dictates ─────────────────────────────

    [Theory]
    [InlineData(0, "otherwise")]
    [InlineData(3, "when")]
    [InlineData("no", "otherwise")]
    [InlineData("yes", "when")]
    public async Task Choice_RoutesByTheSameRule(object value, string expectedBranch)
    {
        string? taken = null;
        var uri = "direct://truth-choice-" + Guid.NewGuid().ToString("N");
        _context.AddRoutes(r => r.From(uri).Choice()
            .When("header.v").Process(_ => taken = "when").EndWhen()
            .Otherwise().Process(_ => taken = "otherwise").EndChoice());
        await _context.Start();

        await Send(uri, value);

        taken.Should().Be(expectedBranch);
    }

    // ── Equality is not truthiness ────────────────────────────────────────────

    /// <summary>
    /// The strict boundary that keeps the total rule safe: 42 and "x" are both truthy, but
    /// 42 == 'x' must stay false — coercion to boolean happens in equality only for explicit
    /// boolean words, bools and numbers.
    /// </summary>
    [Fact]
    public async Task Equality_DoesNotCoerceThroughTruthiness()
    {
        var passed = false;
        _context.AddRoutes(r => r.From("direct://truth-eq").Filter("header.v == 'x'").Process(_ => passed = true));
        await _context.Start();

        await Send("direct://truth-eq", 42);

        passed.Should().BeFalse("42 == 'x' compares values, not truthiness");
    }
}
