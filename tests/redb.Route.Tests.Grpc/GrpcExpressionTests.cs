using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// Tests for expression resolution in gRPC producer options (Phase 2).
/// Verifies that DeadlineExpression binds and resolves correctly.
/// </summary>
public class GrpcExpressionTests
{
    // ── BindFromUri: expression binds to *Expression property ────────

    [Fact]
    public void BindFromUri_DeadlineExpression_BindsCorrectly()
    {
        var opts = new GrpcEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["deadlineExpression"] = "${header.timeout}"
        });

        opts.DeadlineExpression.Should().Be("${header.timeout}");
        opts.Deadline.Should().Be(30_000); // default unchanged
    }

    // ── ResolveOption: deadline expression resolves from exchange ────

    [Fact]
    public void ResolveOption_DeadlineExpression_ResolvesFromExchange()
    {
        var opts = new GrpcEndpointOptions { DeadlineExpression = "${header.timeout}" };
        var msg = new Message("body");
        msg.Headers["timeout"] = "5000";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.DeadlineExpression, exchange);
        resolved.Should().Be("5000");
        int.TryParse(resolved, out var deadline).Should().BeTrue();
        deadline.Should().Be(5000);
    }

    [Fact]
    public void ResolveOption_DeadlineExpression_InvalidValue_TryParseFails()
    {
        var opts = new GrpcEndpointOptions { DeadlineExpression = "${header.timeout}" };
        var msg = new Message("body");
        msg.Headers["timeout"] = "not-a-number";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.DeadlineExpression, exchange);
        resolved.Should().Be("not-a-number");
        int.TryParse(resolved, out _).Should().BeFalse();
    }

    [Fact]
    public void ResolveOption_StaticValue_ReturnsAsIs()
    {
        var opts = new GrpcEndpointOptions();
        var exchange = new Exchange(new Message("body"));

        opts.ResolveOption("plain-string", exchange).Should().Be("plain-string");
    }

    // ── DSL: expression routing in Build ─────────────────────────────

    [Fact]
    public void DslBuild_DeadlineExpression_RoutesToExpressionParam()
    {
        var uri = GrpcDsl.Call("localhost:50051")
            .Deadline(new HeaderExpression("timeout"))
            .Build();

        uri.Should().Contain("deadlineExpression=");
        uri.Should().NotContain("&deadline=");
    }

    [Fact]
    public void DslBuild_DeadlineStatic_RoutesToNormalParam()
    {
        var uri = GrpcDsl.Call("localhost:50051")
            .Deadline(10_000)
            .Build();

        uri.Should().Contain("deadline=10000");
        uri.Should().NotContain("deadlineExpression");
    }

    [Fact]
    public void DslBuild_DeadlineExpression_FullRoundtrip()
    {
        // Build URI with expression
        var uri = GrpcDsl.Call("localhost:50051")
            .Deadline(new HeaderExpression("dlTimeout"))
            .Plaintext()
            .Build();

        // Parse URI into options
        var opts = new GrpcEndpointOptions();
        var queryString = uri.Substring(uri.IndexOf('?') + 1);
        var queryParams = System.Web.HttpUtility.ParseQueryString(queryString);
        var dict = new Dictionary<string, string>();
        foreach (string key in queryParams)
            dict[key] = queryParams[key]!;

        opts.BindFromUri(dict);

        // Verify expression bound correctly
        opts.DeadlineExpression.Should().NotBeNull();
        opts.DeadlineExpression.Should().Contain("${");

        // Resolve against exchange
        var msg = new Message("body");
        msg.Headers["dlTimeout"] = "15000";
        var exchange = new Exchange(msg);

        var resolved = opts.ResolveOption(opts.DeadlineExpression!, exchange);
        resolved.Should().Be("15000");
    }
}
