using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.TestKit;
using redb.Route.Tests.Core.DiscoveryFixtures;
using redb.Route.Tests.Core.DiscoveryFixtures.Injected;
using redb.Route.Tests.Core.DiscoveryFixtures.NoDefaultConstructor;
using redb.Route.Tests.Core.DiscoveryFixtures.Throwing;

namespace redb.Route.Tests.Core
{
    /// <summary>
    /// <c>AddRoutes(Assembly[])</c> on the context and <c>AddRouteBuilders(Assembly[])</c> in the host: which types are
    /// taken, in what order, and that nothing is skipped or duplicated silently. The fixtures live in their own
    /// namespaces so the filter can take exactly them from this assembly, which holds many other builders.
    /// </summary>
    public class RouteBuilderDiscoveryTests
    {
        private static readonly Assembly ThisAssembly = typeof(RouteBuilderDiscoveryTests).Assembly;
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

        private static Func<Type, bool> InNamespace(string ns) => t => t.Namespace == ns;

        private const string Plain = "redb.Route.Tests.Core.DiscoveryFixtures";

        [Fact]
        public void Find_TakesPublicConcreteClosedBuilders_ByFullName()
        {
            var types = RouteBuilderDiscovery.FindBuilderTypes([ThisAssembly], InNamespace(Plain));

            types.Should().Equal(
                [typeof(AlphaRoutes), typeof(BetaRoutes), typeof(Holder.NestedRoutes)],
                "abstract, open generic and non-public builders are not candidates; the order is by full name");
        }

        [Fact]
        public void Find_TheCoreAssembly_HasNoCandidate()
        {
            // RouteBuilder is abstract and InlineRouteBuilder is excluded: scanning the engine adds nothing.
            RouteBuilderDiscovery.FindBuilderTypes([typeof(RouteContext).Assembly], null).Should().BeEmpty();
        }

        [Fact]
        public void Find_WithoutAnAssembly_Throws()
        {
            var act = () => RouteBuilderDiscovery.FindBuilderTypes([], null);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public async Task AddRoutes_StartsTheDiscoveredRoutes()
        {
            await using var ctx = new RouteContext();
            ctx.AddRoutes([ThisAssembly], InNamespace(Plain));
            await ctx.Start();

            await ctx.SendBody("direct://discovery-alpha", "a");
            await ctx.SendBody("direct://discovery-nested", "n");

            await ctx.Mock("mock://discovery-alpha").ExpectBodies("a").AssertIsSatisfiedAsync(Wait);
            await ctx.Mock("mock://discovery-nested").ExpectBodies("n").AssertIsSatisfiedAsync(Wait);
        }

        [Fact]
        public void AddRoutes_FilterLeavesABuilderOut()
        {
            using var ctx = new RouteContext();
            ctx.AddRoutes([ThisAssembly], t => InNamespace(Plain)(t) && t != typeof(BetaRoutes));

            ctx.RouteBuilders.Select(b => b.GetType()).Should().Equal(typeof(AlphaRoutes), typeof(Holder.NestedRoutes));
        }

        [Fact]
        public void AddRoutes_WithoutAParameterlessConstructor_ThrowsAndAddsNothing()
        {
            using var ctx = new RouteContext();
            var act = () => ctx.AddRoutes([ThisAssembly],
                t => InNamespace(Plain)(t) || InNamespace(Plain + ".NoDefaultConstructor")(t));

            act.Should().Throw<InvalidOperationException>().WithMessage($"*{typeof(NeedsAnArgumentRoutes).FullName}*");
            ctx.RouteBuilders.Should().BeEmpty("every builder is created before any is added");
        }

        [Fact]
        public void AddRoutes_AConstructorThatThrows_ThrowsWithTheTypeAndTheCause()
        {
            using var ctx = new RouteContext();
            var act = () => ctx.AddRoutes([ThisAssembly], InNamespace(Plain + ".Throwing"));

            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"*{typeof(ThrowingRoutes).FullName}*")
                .WithInnerException<NotSupportedException>();
            ctx.RouteBuilders.Should().BeEmpty();
        }

        [Fact]
        public void AddRoutes_ATypeAlreadyAddedExplicitly_Throws()
        {
            using var ctx = new RouteContext();
            ctx.AddRoutes(new AlphaRoutes());

            var act = () => ctx.AddRoutes([ThisAssembly], InNamespace(Plain));

            act.Should().Throw<InvalidOperationException>().WithMessage($"*{typeof(AlphaRoutes).FullName}*");
            ctx.RouteBuilders.Should().ContainSingle("the discovery added nothing");
        }

        [Fact]
        public void AddRoutes_ADiscoveredTypeAddedExplicitly_Throws()
        {
            using var ctx = new RouteContext();
            ctx.AddRoutes([ThisAssembly], InNamespace(Plain));

            var act = () => ctx.AddRoutes(new AlphaRoutes());

            act.Should().Throw<InvalidOperationException>().WithMessage($"*{typeof(AlphaRoutes).FullName}*");
        }

        [Fact]
        public void AddRoutes_TheSameAssemblyTwice_Throws()
        {
            using var ctx = new RouteContext();
            ctx.AddRoutes([ThisAssembly], InNamespace(Plain));

            var act = () => ctx.AddRoutes([ThisAssembly], InNamespace(Plain));

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void AddRoutes_TwoInstancesOfAnExplicitType_StillAllowed()
        {
            // A builder with constructor arguments is legitimately added twice with different ones; only discovery
            // claims a type for itself.
            using var ctx = new RouteContext();
            ctx.AddRoutes(new NeedsAnArgumentRoutes("one"));
            ctx.AddRoutes(new NeedsAnArgumentRoutes("two"));

            ctx.RouteBuilders.Should().HaveCount(2);
        }

        [Fact]
        public void AddRouteBuilders_InjectsTheConstructor()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new DiscoveryGreeting("hello"));
            services.AddRedbRoute(route => route.AddRouteBuilders([ThisAssembly], InNamespace(Plain + ".Injected")));
            using var sp = services.BuildServiceProvider();

            var ctx = sp.GetRequiredService<RouteContext>();
            foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
                configurator.Configure(ctx);

            ctx.RouteBuilders.Should().ContainSingle()
                .Which.Should().BeOfType<InjectedRoutes>()
                .Which.Greeting.Text.Should().Be("hello");
            sp.GetRequiredService<RouteBuilder>().Should().BeSameAs(sp.GetRequiredService<InjectedRoutes>());
        }

        [Fact]
        public void AddRouteBuilders_ATypeAlreadyRegistered_Throws()
        {
            var services = new ServiceCollection();
            var act = () => services.AddRedbRoute(route => route
                .AddRouteBuilder<AlphaRoutes>()
                .AddRouteBuilders([ThisAssembly], InNamespace(Plain)));

            act.Should().Throw<InvalidOperationException>().WithMessage($"*{typeof(AlphaRoutes).FullName}*");
        }

        [Fact]
        public void AddRouteBuilder_ADiscoveredType_Throws()
        {
            var services = new ServiceCollection();
            var act = () => services.AddRedbRoute(route => route
                .AddRouteBuilders([ThisAssembly], InNamespace(Plain))
                .AddRouteBuilder<AlphaRoutes>());

            act.Should().Throw<InvalidOperationException>().WithMessage($"*{typeof(AlphaRoutes).FullName}*");
        }
    }
}

namespace redb.Route.Tests.Core.DiscoveryFixtures
{
    public sealed class AlphaRoutes : RouteBuilder
    {
        protected override void Configure() => From("direct://discovery-alpha").To("mock://discovery-alpha");
    }

    public sealed class BetaRoutes : RouteBuilder
    {
        protected override void Configure() => From("direct://discovery-beta").To("mock://discovery-beta");
    }

    public static class Holder
    {
        public sealed class NestedRoutes : RouteBuilder
        {
            protected override void Configure() => From("direct://discovery-nested").To("mock://discovery-nested");
        }
    }

    public abstract class AbstractRoutes : RouteBuilder;

    public sealed class OpenGenericRoutes<T> : RouteBuilder
    {
        protected override void Configure() => From($"direct://discovery-{typeof(T).Name}").To("mock://discovery-generic");
    }

    internal sealed class InternalRoutes : RouteBuilder
    {
        protected override void Configure() => From("direct://discovery-internal").To("mock://discovery-internal");
    }

    public sealed record DiscoveryGreeting(string Text);
}

namespace redb.Route.Tests.Core.DiscoveryFixtures.NoDefaultConstructor
{
    public sealed class NeedsAnArgumentRoutes(string name) : RouteBuilder
    {
        protected override void Configure() => From($"direct://discovery-arg-{name}").To("mock://discovery-arg");
    }
}

namespace redb.Route.Tests.Core.DiscoveryFixtures.Throwing
{
    public sealed class ThrowingRoutes : RouteBuilder
    {
        public ThrowingRoutes() => throw new NotSupportedException("not here");

        protected override void Configure() { }
    }
}

namespace redb.Route.Tests.Core.DiscoveryFixtures.Injected
{
    public sealed class InjectedRoutes(DiscoveryGreeting greeting) : RouteBuilder
    {
        public DiscoveryGreeting Greeting { get; } = greeting;

        protected override void Configure() => From("direct://discovery-injected").To("mock://discovery-injected");
    }
}
