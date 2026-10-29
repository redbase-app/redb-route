namespace redb.Route.Tests.File;

/// <summary>
/// A fact about behavior only Windows has. Elsewhere it is skipped with the documented reason, never silently passed.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    /// <param name="reason">Why the behavior is Windows only, as the README documents it.</param>
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
            Skip = reason;
    }
}
