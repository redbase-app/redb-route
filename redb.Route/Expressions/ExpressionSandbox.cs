namespace redb.Route.Expressions;

/// <summary>
/// Restricts what an expression may do while the scope is open: no method invocation through
/// reflection on the objects an expression reaches. Built-in functions and the string helpers
/// (<c>contains</c>, <c>substring</c>, <c>length</c>, ...) stay available. Opened by hosts that
/// evaluate expressions written as data — a payload template's <c>expr()</c> and its arguments —
/// so a template can read members but never run code. Flows with the async context and nests.
/// </summary>
public static class ExpressionSandbox
{
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>Whether a sandbox scope is open on the current flow.</summary>
    public static bool IsActive => Depth.Value > 0;

    /// <summary>Opens a scope; dispose the result to leave it.</summary>
    public static IDisposable Enter()
    {
        Depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _left;

        public void Dispose()
        {
            if (_left) return;
            _left = true;
            Depth.Value--;
        }
    }
}
