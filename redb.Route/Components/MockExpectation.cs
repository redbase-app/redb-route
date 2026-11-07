using System.Globalization;
using redb.Route.Abstractions;

namespace redb.Route.Components;

/// <summary>
/// What a <see cref="MockEndpoint"/> captured when a message arrived: the live exchange plus the
/// body, headers and properties <b>as they were at that moment</b>. Built-in expectations compare
/// against the captured values, so a step that mutates the exchange after the mock cannot change
/// what the mock "saw" (Apache Camel's mock copies the exchange for the same reason).
/// </summary>
internal sealed class MockReceived
{
    public required IExchange Exchange { get; init; }
    public required object? Body { get; init; }
    public required IReadOnlyDictionary<string, object?> Headers { get; init; }
    public required IReadOnlyDictionary<string, object?> Properties { get; init; }

    public static MockReceived Capture(IExchange exchange) => new()
    {
        Exchange = exchange,
        Body = exchange.In.Body,
        Headers = new Dictionary<string, object?>(exchange.In.Headers, StringComparer.OrdinalIgnoreCase),
        Properties = new Dictionary<string, object?>(exchange.Properties),
    };
}

/// <summary>
/// One expectation registered on a <see cref="MockEndpoint"/>. Evaluated against the captured
/// messages; returns <c>null</c> when satisfied, otherwise one human-readable failure line in the
/// Apache Camel style (<c>Received message count. Expected: 1 but was: 0</c>).
/// </summary>
internal abstract class MockExpectation
{
    public abstract ValueTask<string?> EvaluateAsync(IReadOnlyList<MockReceived> received);
}

/// <summary>Exact or minimum number of received messages.</summary>
internal sealed class MessageCountExpectation(int expected, bool minimum) : MockExpectation
{
    public bool IsMinimum => minimum;

    public override ValueTask<string?> EvaluateAsync(IReadOnlyList<MockReceived> received)
    {
        var ok = minimum ? received.Count >= expected : received.Count == expected;
        if (ok) return ValueTask.FromResult<string?>(null);
        var kind = minimum ? "Expected at least" : "Expected";
        return ValueTask.FromResult<string?>($"Received message count. {kind}: {expected} but was: {received.Count}");
    }
}

/// <summary>Bodies received, in order or in any order.</summary>
internal sealed class BodiesExpectation(object?[] bodies, bool anyOrder) : MockExpectation
{
    public override ValueTask<string?> EvaluateAsync(IReadOnlyList<MockReceived> received)
    {
        if (received.Count != bodies.Length)
            return ValueTask.FromResult<string?>(
                $"Received message count. Expected: {bodies.Length} but was: {received.Count}");

        if (!anyOrder)
        {
            for (var i = 0; i < bodies.Length; i++)
            {
                if (!MockValues.AreEqual(bodies[i], received[i].Body))
                    return ValueTask.FromResult<string?>(
                        $"Body of message {i + 1}. Expected: {MockValues.Describe(bodies[i])} but was: {MockValues.Describe(received[i].Body)}");
            }
            return ValueTask.FromResult<string?>(null);
        }

        var remaining = received.Select(r => r.Body).ToList();
        foreach (var expected in bodies)
        {
            var index = remaining.FindIndex(actual => MockValues.AreEqual(expected, actual));
            if (index < 0)
                return ValueTask.FromResult<string?>(
                    $"Body {MockValues.Describe(expected)} expected in any order but was not received");
            remaining.RemoveAt(index);
        }
        return ValueTask.FromResult<string?>(null);
    }
}

/// <summary>A header (or exchange property) with a given value on every message, or on at least one.</summary>
internal sealed class KeyValueExpectation(string kind, string key, object? value, bool every) : MockExpectation
{
    public override ValueTask<string?> EvaluateAsync(IReadOnlyList<MockReceived> received)
    {
        if (received.Count == 0)
            return ValueTask.FromResult<string?>($"{kind} '{key}'. Expected: {MockValues.Describe(value)} but no message was received");

        for (var i = 0; i < received.Count; i++)
        {
            var store = kind == "Property" ? received[i].Properties : received[i].Headers;
            var present = store.TryGetValue(key, out var actual);
            var matches = present && MockValues.AreEqual(value, actual);

            if (matches && !every) return ValueTask.FromResult<string?>(null);
            if (!matches && every)
                return ValueTask.FromResult<string?>(
                    $"{kind} '{key}' on message {i + 1}. Expected: {MockValues.Describe(value)} but was: {(present ? MockValues.Describe(actual) : "<absent>")}");
        }

        return ValueTask.FromResult<string?>(every
            ? null
            : $"{kind} '{key}'. Expected: {MockValues.Describe(value)} on at least one message but none matched");
    }
}

/// <summary>An arbitrary predicate holding on every message (evaluated against the live exchange).</summary>
internal sealed class PredicateExpectation(Func<IExchange, Task<bool>> predicate, string description) : MockExpectation
{
    public override async ValueTask<string?> EvaluateAsync(IReadOnlyList<MockReceived> received)
    {
        if (received.Count == 0)
            return $"Predicate {description}. Expected to hold but no message was received";

        for (var i = 0; i < received.Count; i++)
        {
            if (!await predicate(received[i].Exchange).ConfigureAwait(false))
                return $"Predicate {description} failed on message {i + 1}: {MockValues.Describe(received[i].Body)}";
        }
        return null;
    }
}

/// <summary>Value comparison and formatting shared by the expectations.</summary>
internal static class MockValues
{
    /// <summary>
    /// <see cref="object.Equals(object?, object?)"/> plus two conveniences that make expectations
    /// read naturally: numbers compare across CLR types (<c>1</c> equals <c>1L</c>), and byte arrays
    /// compare by content.
    /// </summary>
    public static bool AreEqual(object? expected, object? actual)
    {
        if (Equals(expected, actual)) return true;
        if (expected is null || actual is null) return false;

        if (IsNumeric(expected) && IsNumeric(actual))
        {
            try
            {
                return Convert.ToDecimal(expected, CultureInfo.InvariantCulture)
                    == Convert.ToDecimal(actual, CultureInfo.InvariantCulture);
            }
            catch (OverflowException) { return false; }
        }

        if (expected is byte[] eb && actual is byte[] ab)
            return eb.AsSpan().SequenceEqual(ab);

        return false;
    }

    public static string Describe(object? value) => value switch
    {
        null => "<null>",
        string s => $"\"{s}\"",
        byte[] b => $"byte[{b.Length}]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? value.GetType().Name,
    };

    private static bool IsNumeric(object value) => value is sbyte or byte or short or ushort or int or uint
        or long or ulong or float or double or decimal;
}
