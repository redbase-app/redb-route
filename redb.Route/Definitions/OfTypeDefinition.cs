using redb.Route.Abstractions;
using redb.Route.Processors;
using redb.Route.Serialization;

namespace redb.Route.Definitions;

/// <summary>
/// Typed scope-opener that converts the exchange body to <typeparamref name="T"/>
/// (via <see cref="ConvertBodyProcessor"/>) and then runs the contained child outputs
/// with the body strongly typed as <typeparamref name="T"/>. Apache Camel parity:
/// <c>.OfType&lt;T&gt;()</c> equivalent of <c>choice().when(body().isInstanceOf(T.class))</c>
/// combined with a transparent unmarshal step.
/// </summary>
public class OfTypeDefinition<T> : RouteDefinition
{
    internal OfTypeDefinition() { }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var pipeline = new PipelineProcessor();

        // Skip ConvertBody when target type is string and body may not be string —
        // ConvertBodyProcessor handles the "already-of-target-type" pass-through itself,
        // but we elide it for the trivial string passthrough to match Camel semantics.
        if (typeof(T) != typeof(string))
            pipeline.Add(new ConvertBodyProcessor(typeof(T), context.GetService<IDataFormatRegistry>()));

        foreach (var output in Outputs)
            pipeline.Add(output.CreateProcessor(context));

        return pipeline;
    }

    // ── Typed overloads ────────────────────────────────────────────────────────

    /// <summary>Typed Process: invoked with the body cast to <typeparamref name="T"/>.</summary>
    public OfTypeDefinition<T> Process(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        AddOutput(new ProcessActionDefinition(e =>
        {
            if (e.In.Body is T typed) action(typed);
        }));
        return this;
    }

    /// <summary>Typed Transform: receives the body as <typeparamref name="T"/> and assigns the result back.</summary>
    public OfTypeDefinition<T> Transform(Func<T, object?> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        AddOutput(new TransformDefinition(e =>
            e.In.Body is T typed ? transform(typed) : e.In.Body));
        return this;
    }

    /// <summary>Typed inline Filter: keep only exchanges whose body matches the typed predicate.</summary>
    public OfTypeFilterDefinition<T> Filter(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var def = new OfTypeFilterDefinition<T>(e => e.In.Body is T typed && predicate(typed));
        AddOutput(def);
        return def;
    }
}

/// <summary>
/// Typed filter scope used inside an <see cref="OfTypeDefinition{T}"/>. Re-exposes typed
/// <see cref="Process(Action{T})"/> and <see cref="Transform(Func{T, object?})"/> overloads.
/// </summary>
public sealed class OfTypeFilterDefinition<T> : FilterDefinition
{
    internal OfTypeFilterDefinition(Func<IExchange, bool> predicate) : base(predicate) { }

    /// <summary>Typed Process inside the filter scope.</summary>
    public new OfTypeFilterDefinition<T> Process(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        AddOutput(new ProcessActionDefinition(e =>
        {
            if (e.In.Body is T typed) action(typed);
        }));
        return this;
    }

    /// <summary>Typed Transform inside the filter scope.</summary>
    public new OfTypeFilterDefinition<T> Transform(Func<T, object?> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        AddOutput(new TransformDefinition(e =>
            e.In.Body is T typed ? transform(typed) : e.In.Body));
        return this;
    }
}
