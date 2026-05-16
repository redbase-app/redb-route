using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Tests.Extensions;

/// <summary>
/// Tests for <see cref="RouteContextExtensions.AddComponents"/> and convenience constructors.
/// </summary>
public class RouteContextExtensionsTests
{
    // ─── AddComponents ───────────────────────────────────────────────

    [Fact]
    public void AddComponents_RegistersComponentsFromAssembly()
    {
        var ctx = new RouteContext();
        ctx.AddComponents(typeof(StubComponentA).Assembly);

        ctx.HasComponent("stub-a").Should().BeTrue();
        ctx.HasComponent("stub-b").Should().BeTrue();
    }

    [Fact]
    public void AddComponents_SkipsAlreadyRegistered()
    {
        var ctx = new RouteContext();

        // "direct" is built-in
        ctx.HasComponent("direct").Should().BeTrue();

        // scanning this test assembly should not throw or overwrite built-in
        ctx.AddComponents(typeof(StubComponentA).Assembly);
        ctx.HasComponent("direct").Should().BeTrue();
    }

    [Fact]
    public void AddComponents_SkipsAbstractAndInterface()
    {
        var ctx = new RouteContext();
        ctx.AddComponents(typeof(AbstractStubComponent).Assembly);

        ctx.HasComponent("abstract-stub").Should().BeFalse();
    }

    [Fact]
    public void AddComponents_SkipsTypesRequiringCtorArgs()
    {
        var ctx = new RouteContext();

        // Should not throw even though CtorArgComponent requires args
        ctx.Invoking(c => c.AddComponents(typeof(CtorArgComponent).Assembly))
           .Should().NotThrow();
    }

    [Fact]
    public void AddComponents_SetsContextOnComponentBase()
    {
        var ctx = new RouteContext();
        ctx.AddComponents(typeof(StubComponentA).Assembly);

        var comp = ctx.GetComponent<StubComponentA>();
        comp.Should().NotBeNull();
        comp!.Context.Should().BeSameAs(ctx);
    }

    [Fact]
    public void AddComponents_ReturnsSameContext_ForFluent()
    {
        var ctx = new RouteContext();
        var result = ctx.AddComponents(typeof(StubComponentA).Assembly);

        result.Should().BeSameAs(ctx);
    }

    [Fact]
    public void AddComponents_MultipleAssemblies()
    {
        var ctx = new RouteContext();
        ctx.AddComponents(typeof(StubComponentA).Assembly, typeof(RouteContext).Assembly);

        ctx.HasComponent("stub-a").Should().BeTrue();
    }

    // ─── Convenience constructor ─────────────────────────────────────

    [Fact]
    public void ServiceProviderCtor_RegistersProvider()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var ctx = new RouteContext(sp);

        ctx.GetServiceProvider().Should().BeSameAs(sp);
    }

    [Fact]
    public void ServiceProviderCtor_RegistersBuiltInComponents()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var ctx = new RouteContext(sp, contextId: "test-sp");

        ctx.ContextId.Should().Be("test-sp");
        ctx.HasComponent("direct").Should().BeTrue();
        ctx.HasComponent("seda").Should().BeTrue();
        ctx.HasComponent("timer").Should().BeTrue();
        ctx.HasComponent("mock").Should().BeTrue();
        ctx.HasComponent("log").Should().BeTrue();
    }

    [Fact]
    public void ServiceProviderCtor_ThrowsOnNullProvider()
    {
        Action act = () => new RouteContext(serviceProvider: null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ─── Stub components for assembly scanning ───────────────────────

    public class StubComponentA : ComponentBase
    {
        public override string Scheme => "stub-a";
        public override IEndpoint CreateEndpoint(EndpointUri uri) => throw new NotSupportedException();
    }

    public class StubComponentB : ComponentBase
    {
        public override string Scheme => "stub-b";
        public override IEndpoint CreateEndpoint(EndpointUri uri) => throw new NotSupportedException();
    }

    public abstract class AbstractStubComponent : ComponentBase
    {
        public override string Scheme => "abstract-stub";
        public override IEndpoint CreateEndpoint(EndpointUri uri) => throw new NotSupportedException();
    }

    public class CtorArgComponent : ComponentBase
    {
        private readonly string _required;
        public CtorArgComponent(string required) => _required = required;
        public override string Scheme => "ctor-arg";
        public override IEndpoint CreateEndpoint(EndpointUri uri) => throw new NotSupportedException();
    }
}
