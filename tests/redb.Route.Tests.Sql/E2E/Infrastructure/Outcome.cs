namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// Runs an operation whose failure is the observed fact, not a test error: the exception is returned so the
/// test can assert on it. Nothing is swallowed — every caller asserts on the result.
/// </summary>
public static class Outcome
{
    /// <summary>The exception the operation threw, or null when it completed.</summary>
    public static async Task<Exception?> Of(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Short description for assertion messages.</summary>
    public static string Describe(Exception? exception) =>
        exception is null ? "no exception" : $"{exception.GetType().Name}: {exception.Message.Split('\n')[0].Trim()}";
}
