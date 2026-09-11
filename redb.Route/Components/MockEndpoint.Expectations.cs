using System.Diagnostics;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Predicates;

namespace redb.Route.Components;

/// <summary>
/// Expectations, assertions and scripted responses of the mock endpoint (Apache Camel
/// <c>MockEndpoint</c> parity). Expectations are fluent and cumulative; <see cref="AssertIsSatisfiedAsync"/>
/// waits until all of them hold or the timeout elapses and then fails with every unmet expectation
/// and the bodies actually received.
/// </summary>
public partial class MockEndpoint
{
    private readonly object _sync = new();
    private readonly List<MockExpectation> _expectations = [];
    private readonly List<MockResponse> _responses = [];
    private readonly List<MockReceived> _captured = [];
    private TaskCompletionSource<bool> _changed = NewSignal();
    private int _messageNumber;

    // ── Receive path (called by MockProducer) ─────────────────────────────────

    /// <summary>Records the message, wakes waiters, then applies the scripted responses that target it.</summary>
    internal async Task OnMessageAsync(IExchange exchange, CancellationToken ct)
    {
        int number;
        MockResponse[] responses;
        lock (_sync)
        {
            number = ++_messageNumber;
            _captured.Add(MockReceived.Capture(exchange));
            responses = _responses.Where(r => r.AppliesTo(number)).ToArray();
        }
        RecordExchange(exchange);
        Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult(true);

        foreach (var response in responses)
            await response.ApplyAsync(exchange, ct).ConfigureAwait(false);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ResetExpectations()
    {
        lock (_sync)
        {
            _expectations.Clear();
            _responses.Clear();
            _captured.Clear();
            _messageNumber = 0;
        }
    }

    // ── Expectations ──────────────────────────────────────────────────────────

    /// <summary>Expects exactly <paramref name="count"/> messages.</summary>
    public MockEndpoint ExpectMessageCount(int count) => Add(new MessageCountExpectation(Positive(count), minimum: false));

    /// <summary>Expects at least <paramref name="count"/> messages.</summary>
    public MockEndpoint ExpectMinimumMessageCount(int count) => Add(new MessageCountExpectation(Positive(count), minimum: true));

    /// <summary>Expects exactly these bodies, in this order (numbers compare across CLR types).</summary>
    public MockEndpoint ExpectBodies(params object?[] bodies) => Add(new BodiesExpectation(bodies ?? [], anyOrder: false));

    /// <summary>Expects exactly these bodies, in any order.</summary>
    public MockEndpoint ExpectBodiesInAnyOrder(params object?[] bodies) => Add(new BodiesExpectation(bodies ?? [], anyOrder: true));

    /// <summary>Expects every message to carry header <paramref name="name"/> equal to <paramref name="value"/>.</summary>
    public MockEndpoint ExpectHeader(string name, object? value) => Add(new KeyValueExpectation("Header", Required(name), value, every: true));

    /// <summary>Expects at least one message to carry header <paramref name="name"/> equal to <paramref name="value"/>.</summary>
    public MockEndpoint ExpectHeaderReceived(string name, object? value) => Add(new KeyValueExpectation("Header", Required(name), value, every: false));

    /// <summary>Expects every message to carry exchange property <paramref name="key"/> equal to <paramref name="value"/>.</summary>
    public MockEndpoint ExpectProperty(string key, object? value) => Add(new KeyValueExpectation("Property", Required(key), value, every: true));

    /// <summary>Expects <paramref name="predicate"/> to hold on every message.</summary>
    public MockEndpoint Expect(Func<IExchange, bool> predicate, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Add(new PredicateExpectation(e => Task.FromResult(predicate(e)), description ?? "<lambda>"));
    }

    /// <summary>Expects <paramref name="predicate"/> to hold on every message (awaited, so async predicates work).</summary>
    public MockEndpoint Expect(IPredicate predicate, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Add(new PredicateExpectation(predicate.MatchesAsync, description ?? predicate.GetType().Name));
    }

    /// <summary>
    /// Expects the route-language condition (<c>"header.priority == 'high' and body.total > 100"</c>)
    /// to hold on every message; compiled through the same <c>PredicateFactory</c> as <c>Filter(string)</c>.
    /// </summary>
    public MockEndpoint Expect(string condition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        return Add(new PredicateExpectation(PredicateFactory.FromString(condition).MatchesAsync, $"'{condition}'"));
    }

    private MockEndpoint Add(MockExpectation expectation)
    {
        lock (_sync) _expectations.Add(expectation);
        return this;
    }

    private static int Positive(int count) => count >= 0 ? count : throw new ArgumentOutOfRangeException(nameof(count));
    private static string Required(string name) { ArgumentException.ThrowIfNullOrWhiteSpace(name); return name; }

    // ── Scripted responses ────────────────────────────────────────────────────

    /// <summary>Scripts what happens to the <paramref name="messageNumber"/>-th message (1-based): reply body, delay, exception.</summary>
    public MockResponse Whenever(int messageNumber)
    {
        if (messageNumber < 1) throw new ArgumentOutOfRangeException(nameof(messageNumber), "Message numbers are 1-based.");
        var response = new MockResponse(messageNumber);
        lock (_sync) _responses.Add(response);
        return response;
    }

    /// <summary>Scripts what happens to every message.</summary>
    public MockResponse WheneverAny()
    {
        var response = new MockResponse(null);
        lock (_sync) _responses.Add(response);
        return response;
    }

    // ── Assertions ────────────────────────────────────────────────────────────

    /// <summary>Returns <c>true</c> when every expectation currently holds (no waiting).</summary>
    public async Task<bool> IsSatisfiedAsync() => (await EvaluateAsync().ConfigureAwait(false)).Count == 0;

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for every expectation to hold, then throws
    /// <see cref="MockAssertionException"/> listing each unmet expectation and the bodies received.
    /// An <c>expectedMessageCount</c> URI option counts as an expectation when no explicit count was set.
    /// </summary>
    public async Task AssertIsSatisfiedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var signal = Volatile.Read(ref _changed).Task;
            var failures = await EvaluateAsync().ConfigureAwait(false);
            if (failures.Count == 0) return;

            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new MockAssertionException(FormatFailures(failures));

            await Task.WhenAny(signal, Task.Delay(remaining, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Waits the full <paramref name="timeout"/>, then throws if the expectations turned out to be satisfied.</summary>
    public async Task AssertIsNotSatisfiedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        await Task.Delay(timeout, ct).ConfigureAwait(false);
        var failures = await EvaluateAsync().ConfigureAwait(false);
        if (failures.Count == 0)
            throw new MockAssertionException($"{Uri.NormalizedKey} expectations were satisfied but were expected not to be ({ReceivedCount} message(s) received)");
    }

    private async Task<List<string>> EvaluateAsync()
    {
        MockExpectation[] expectations;
        MockReceived[] received;
        lock (_sync)
        {
            expectations = _expectations.ToArray();
            received = _captured.ToArray();
        }

        var effective = expectations.ToList();
        if (_mockOptions.ExpectedMessageCount > 0 && !expectations.Any(e => e is MessageCountExpectation { IsMinimum: false }))
            effective.Insert(0, new MessageCountExpectation(_mockOptions.ExpectedMessageCount, minimum: false));

        var failures = new List<string>();
        foreach (var expectation in effective)
        {
            var failure = await expectation.EvaluateAsync(received).ConfigureAwait(false);
            if (failure is not null) failures.Add(failure);
        }
        return failures;
    }

    private string FormatFailures(List<string> failures)
    {
        MockReceived[] received;
        lock (_sync) received = _captured.ToArray();

        var sb = new StringBuilder();
        foreach (var failure in failures)
            sb.Append(Uri.NormalizedKey).Append(' ').AppendLine(failure);
        sb.Append("  received ").Append(received.Length).AppendLine(" message(s):");
        for (var i = 0; i < received.Length; i++)
            sb.Append("    ").Append(i + 1).Append(": ").AppendLine(MockValues.Describe(received[i].Body));
        return sb.ToString().TrimEnd();
    }
}
