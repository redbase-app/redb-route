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
    public void DSL_AddsSingleChoiceStep()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        def.Normalize(n => n
            .When(e => e.In.Body is string, e => ((string)e.In.Body!).ToUpperInvariant()));

        def.Steps.Should().ContainSingle(s => s is ChoiceStep);
    }

    [Fact]
    public void DSL_NullConfigure_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

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
    // Builder — Build output
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SingleWhen_ProducesChoiceWithOneClause()
    {
        var builder = new NormalizerDefinition();
        builder.When(e => e.In.Body is string, e => "normalized");

        var choice = builder.Build();

        choice.WhenClauses.Should().HaveCount(1);
        choice.WhenClauses[0].Steps.Should().ContainSingle(s => s is TransformStep);
        choice.OtherwiseSteps.Should().BeNull();
    }

    [Fact]
    public void Build_MultipleWhen_ProducesMatchingClauses()
    {
        var builder = new NormalizerDefinition();
        builder.When(e => e.In.Body is string, e => "str");
        builder.When(e => e.In.Body is int, e => "int");
        builder.When(e => e.In.Body is double, e => "dbl");

        var choice = builder.Build();

        choice.WhenClauses.Should().HaveCount(3);
    }

    [Fact]
    public void Build_WithOtherwise_ProducesOtherwiseTransformStep()
    {
        var builder = new NormalizerDefinition();
        builder.When(e => e.In.Body is string, e => "str");
        builder.Otherwise(e => "fallback");

        var choice = builder.Build();

        choice.OtherwiseSteps.Should().NotBeNull();
        choice.OtherwiseSteps.Should().ContainSingle(s => s is TransformStep);
    }

    [Fact]
    public void Build_WhenContentType_ProducesPredicateClause()
    {
        var builder = new NormalizerDefinition();
        builder.WhenContentType("application/json", e => "json");
        builder.WhenContentType("application/xml", e => "xml");

        var choice = builder.Build();

        choice.WhenClauses.Should().HaveCount(2);
        // Each clause should have a TransformStep
        foreach (var clause in choice.WhenClauses)
        {
            clause.Steps.Should().ContainSingle(s => s is TransformStep);
        }
    }

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
