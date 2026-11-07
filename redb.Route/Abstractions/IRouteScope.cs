namespace redb.Route.Abstractions;

/// <summary>
/// Implemented by every scope-opener definition (Filter, Loop, TryCatch, Choice, Split, etc.).
/// Provides a universal <see cref="End"/> method as a shorter alias for the scope-specific
/// <c>EndXxx()</c> methods (e.g. <c>EndLoop()</c>, <c>EndFilter()</c>).
/// </summary>
public interface IRouteScope
{
    /// <summary>
    /// Closes this scope and returns the parent <see cref="IRouteDefinition"/>.
    /// Equivalent to calling the scope-specific <c>EndXxx()</c> method.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown if the scope was not attached to a parent route definition.
    /// </exception>
    IRouteDefinition End();
}
