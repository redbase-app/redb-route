namespace redb.Route.Abstractions;

/// <summary>Severity of an <see cref="IScopeNestingRule"/> verdict.</summary>
public enum NestingPolicy
{
    /// <summary>The nesting is fine.</summary>
    Allowed,

    /// <summary>Allowed but semantically risky — emit a build warning.</summary>
    Warn,

    /// <summary>Structurally ill-formed — fail the build.</summary>
    Forbid
}

/// <summary>Result of evaluating an <see cref="IScopeNestingRule"/> against one enclosing scope.</summary>
/// <param name="Policy">What to do about the nesting.</param>
/// <param name="Message">Human-readable reason (required for Warn/Forbid).</param>
public readonly record struct NestingVerdict(NestingPolicy Policy, string? Message)
{
    /// <summary>No constraint.</summary>
    public static NestingVerdict Allowed => new(NestingPolicy.Allowed, null);

    /// <summary>Allowed but risky — warn with the given message.</summary>
    public static NestingVerdict Warn(string message) => new(NestingPolicy.Warn, message);

    /// <summary>Ill-formed — fail the build with the given message.</summary>
    public static NestingVerdict Forbid(string message) => new(NestingPolicy.Forbid, message);
}

/// <summary>
/// Marker: a <b>branching</b> scope whose body is a bounded sub-region — the exchange is
/// conditionally executed (Filter), branched (Choice), or fanned out (Split/Multicast). A node with
/// linear "tail" semantics (e.g. a replay checkpoint) generally cannot span such a boundary.
/// </summary>
public interface ICompositeScope { }

/// <summary>
/// Marker: a <b>durable/transactional</b> scope (e.g. Transaction). Nesting is structurally fine,
/// but a re-invocation (replay) runs OUTSIDE the original transaction — usually a warn, not a forbid.
/// </summary>
public interface IDurableScope { }

/// <summary>
/// Declarative nesting constraint on a definition — reusable, generic validation instead of ad-hoc
/// per-type checks in the validator. While walking the definition tree the validator calls
/// <see cref="CheckAncestor"/> for each enclosing scope (a node implementing <see cref="IRouteScope"/>);
/// a <see cref="NestingPolicy.Forbid"/> fails the build, a <see cref="NestingPolicy.Warn"/> logs a
/// warning. Any future definition that must constrain where it nests just implements this interface —
/// the validator never changes.
/// <para>
/// Scopes advertise their category with marker interfaces (<see cref="ICompositeScope"/>,
/// <see cref="IDurableScope"/>), so a rule reacts to a category rather than to concrete types.
/// </para>
/// </summary>
public interface IScopeNestingRule
{
    /// <summary>Evaluates this node's policy against one enclosing scope ancestor.</summary>
    /// <param name="ancestor">An enclosing scope definition (nearest and outer scopes are both offered).</param>
    NestingVerdict CheckAncestor(IProcessorDefinition ancestor);
}

/// <summary>
/// Implemented by definitions whose logical children live <b>outside</b>
/// <see cref="IProcessorDefinition.Outputs"/> — e.g. a Choice keeps its <c>When</c>/<c>Otherwise</c>
/// branches in dedicated lists. A generic tree-walk (validation, analysis) recurses into
/// <see cref="Branches"/> so it reaches those children without any per-type knowledge.
/// </summary>
public interface IBranchingDefinition
{
    /// <summary>Child definitions not present in <see cref="IProcessorDefinition.Outputs"/>.</summary>
    IEnumerable<IProcessorDefinition> Branches { get; }
}
