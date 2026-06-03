using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Definitions;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Unit tests for Normalizer DSL, builder validation, and step generation.
/// </summary>
public class NormalizerTests
{
    // ══════════════════════════════════════════════════════════════
    // DSL — step recording
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_AddsNormalizerStep()
    {
        var def = new RouteDefinition();

        def.Normalize(n => n
            .When(e => e.In.Body is string, e => ((string)e.In.Body!).ToUpperInvariant()));

        def.Outputs.Should().ContainSingle().Which.Should().BeOfType<NormalizerDefinition>();
    }

    [Fact]
    public void DSL_NullConfigure_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.Normalize(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DSL_NoWhenClauses_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        var act = () => def.Normalize(n => { });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at least one When*");
    }

    // ══════════════════════════════════════════════════════════════
    // Builder — When clause validation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void When_NullPredicate_Throws()
    {
        var builder = new NormalizerDefinition();

        var act = () => builder.When(null!, e => e.In.Body);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void When_NullTransform_Throws()
    {
        var builder = new NormalizerDefinition();

        var act = () => builder.When(e => true, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenContentType_NullContentType_Throws()
    {
        var builder = new NormalizerDefinition();

        var act = () => builder.WhenContentType(null!, e => e.In.Body);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenContentType_NullTransform_Throws()
    {
        var builder = new NormalizerDefinition();

        var act = () => builder.WhenContentType("application/json", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Otherwise_NullTransform_Throws()
    {
        var builder = new NormalizerDefinition();

        var act = () => builder.Otherwise(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Builder — chaining
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Builder_Chaining_ReturnsSameInstance()
    {
        INormalizerDefinition builder = new NormalizerDefinition();

        var result = builder
            .When(e => true, e => "a")
            .WhenContentType("text/plain", e => "b")
            .Otherwise(e => "c");

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public void DSL_Chaining_ReturnsRouteDefinition()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        var result = def.Normalize(n => n.When(e => true, e => "ok"));

        result.Should().BeSameAs(def);
    }
}
