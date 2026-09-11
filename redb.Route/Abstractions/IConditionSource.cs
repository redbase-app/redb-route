namespace redb.Route.Abstractions;

/// <summary>
/// Declarative capture of the condition a scope was built from, for definitions whose behaviour is
/// driven by a condition: Filter and the When branch of a Choice.
/// <para>
/// The DSL offers three ways to write a condition, and this contract keeps one name and one fixed
/// type for each of them, so a reader never has to guess what a property holds:
/// </para>
/// <list type="table">
///   <item>
///     <term><see cref="SourcePredicate"/></term>
///     <description><see cref="IPredicate"/> — the condition was written as a predicate instance,
///       or was compiled from a condition string into one.</description>
///   </item>
///   <item>
///     <term><see cref="SourceExpression"/></term>
///     <description><see cref="IExpression"/> — the condition was written as an expression
///       instance.</description>
///   </item>
///   <item>
///     <term><see cref="SourceTemplate"/></term>
///     <description><see cref="string"/> — the condition string exactly as the author wrote it.
///       </description>
///   </item>
/// </list>
/// <para>
/// A condition written as a plain delegate leaves all three null: a lambda has no source to
/// capture. A condition written as a string sets <see cref="SourceTemplate"/> and, since it is
/// compiled into one, <see cref="SourcePredicate"/>.
/// </para>
/// <para>
/// Only reading is offered. The properties are filled while the route is being built and are not
/// meant to be rewritten afterwards.
/// </para>
/// </summary>
public interface IConditionSource
{
    /// <summary>The predicate the condition was built from, or built into; null if there is none.</summary>
    IPredicate? SourcePredicate { get; }

    /// <summary>The expression instance the condition was built from; null if there is none.</summary>
    IExpression? SourceExpression { get; }

    /// <summary>The condition string as the author wrote it; null if the condition was not written as a string.</summary>
    string? SourceTemplate { get; }
}
