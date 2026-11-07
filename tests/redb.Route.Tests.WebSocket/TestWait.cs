namespace redb.Route.Tests.WebSocket;

/// <summary>
/// Waits for a condition instead of a fixed pause. A fixed pause is too short on a slow machine and makes the
/// test fail at random; this wait ends as soon as the condition holds.
/// </summary>
internal static class TestWait
{
    /// <summary>
    /// Polls <paramref name="done"/> every 20 ms until it returns true or <paramref name="timeoutMs"/> elapses.
    /// At the timeout it simply returns, so the assertion that follows fails with its own message.
    /// </summary>
    public static async Task Until(Func<bool> done, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }
}
