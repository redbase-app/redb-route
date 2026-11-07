using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Extensions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// A <c>${prefix.rest}</c> resolver registered on a route context serves the expressions of that context's exchanges
/// only, and goes with the context: nothing of it is left in the process-wide resolvers.
/// </summary>
public sealed class ContextPrefixResolverTests
{
    private static Exchange On(RouteContext context) => new(new Message("body")) { Context = context };

    [Fact]
    public void An_exchange_of_the_context_resolves_the_prefix_through_the_context()
    {
        using var context = new RouteContext();
        context.RegisterPrefixResolver("order.", (_, rest) => $"order:{rest}");

        ExpressionResolver.ProcessTemplate("${order.id}", On(context)).Should().Be("order:id");
    }

    [Fact]
    public void An_exchange_of_another_context_does_not_see_the_prefix()
    {
        using var owner = new RouteContext();
        using var other = new RouteContext();
        owner.RegisterPrefixResolver("tenant.", (_, _) => "owner");

        var exchange = On(other);
        exchange.Properties["tenant.name"] = "plain property";

        ExpressionResolver.ProcessTemplate("${tenant.name}", exchange).Should().Be("plain property");
    }

    [Fact]
    public void The_context_prefix_wins_over_a_process_wide_one_and_leaves_it_in_place()
    {
        var prefix = $"shadow{Guid.NewGuid():N}.";
        ExpressionResolver.RegisterPrefixResolver(prefix, (_, _) => "process");
        try
        {
            using var context = new RouteContext();
            context.RegisterPrefixResolver(prefix, (_, _) => "context");

            ExpressionResolver.ProcessTemplate($"${{{prefix}x}}", On(context)).Should().Be("context");
            ExpressionResolver.ProcessTemplate($"${{{prefix}x}}", new Exchange(new Message("b"))).Should().Be("process");
        }
        finally
        {
            ExpressionResolver.UnregisterPrefixResolver(prefix);
        }
    }

    [Fact]
    public void The_resolvers_are_a_service_of_the_context_and_one_per_context()
    {
        using var context = new RouteContext();

        var first = ExpressionPrefixResolvers.For(context);

        ExpressionPrefixResolvers.For(context).Should().BeSameAs(first);
        context.GetService<ExpressionPrefixResolvers>().Should().BeSameAs(first);
        var bad = () => first.Register("noDot", (_, _) => null);
        bad.Should().Throw<ArgumentException>();
    }
}
