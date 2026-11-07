using redb.Route.Abstractions;

namespace redb.Route.Components;

/// <summary>
/// Scripted behaviour of a <see cref="MockEndpoint"/> for one message (<see cref="MockEndpoint.Whenever"/>)
/// or for every message (<see cref="MockEndpoint.WheneverAny"/>): set the reply body or a header,
/// delay, throw, or run arbitrary code. Actions run in the order they were added, after the exchange
/// has been recorded, so a throwing mock still counts the message it rejected.
/// <para>
/// This is what makes <c>mock://</c> usable behind <c>Enrich</c> / <c>PollEnrich</c> / request-reply:
/// the reply body is whatever <see cref="SetBody"/> left in the exchange.
/// </para>
/// </summary>
public sealed class MockResponse
{
    private readonly List<Func<IExchange, CancellationToken, Task>> _actions = [];

    internal MockResponse(int? messageNumber)
    {
        MessageNumber = messageNumber;
    }

    /// <summary>1-based ordinal of the message this response applies to; <c>null</c> = every message.</summary>
    public int? MessageNumber { get; }

    /// <summary>Replaces the message body (the reply seen by <c>Enrich</c> / request-reply callers).</summary>
    public MockResponse SetBody(object? body)
    {
        _actions.Add((exchange, _) => { exchange.In.Body = body; return Task.CompletedTask; });
        return this;
    }

    /// <summary>Sets a header on the message.</summary>
    public MockResponse SetHeader(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _actions.Add((exchange, _) => { exchange.In.Headers[name] = value; return Task.CompletedTask; });
        return this;
    }

    /// <summary>Waits before continuing — simulates a slow downstream system.</summary>
    public MockResponse Delay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        _actions.Add((_, ct) => Task.Delay(delay, ct));
        return this;
    }

    /// <summary>Throws the given exception — simulates a failing downstream system.</summary>
    public MockResponse Throw(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _actions.Add((_, _) => throw exception);
        return this;
    }

    /// <summary>Throws a new <typeparamref name="TException"/> with the given message.</summary>
    public MockResponse Throw<TException>(string message) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(message);
        _actions.Add((_, _) => throw (Exception)Activator.CreateInstance(typeof(TException), message)!);
        return this;
    }

    /// <summary>Runs arbitrary synchronous code against the exchange.</summary>
    public MockResponse Do(Action<IExchange> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions.Add((exchange, _) => { action(exchange); return Task.CompletedTask; });
        return this;
    }

    /// <summary>Runs arbitrary asynchronous code against the exchange.</summary>
    public MockResponse Do(Func<IExchange, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions.Add(action);
        return this;
    }

    internal bool AppliesTo(int messageNumber) => MessageNumber is null || MessageNumber == messageNumber;

    internal async Task ApplyAsync(IExchange exchange, CancellationToken ct)
    {
        foreach (var action in _actions)
            await action(exchange, ct).ConfigureAwait(false);
    }
}
