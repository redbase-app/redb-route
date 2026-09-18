using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Sql.Batch.Fakes;

/// <summary>An exchange that delegates to a real one and counts the linked children created from it.</summary>
internal sealed class CountingExchange(Exchange inner) : IExchange
{
    /// <summary>Calls of <see cref="CreateLinkedChild"/>.</summary>
    public int LinkedChildren { get; private set; }

    public IMessage In { get => inner.In; set => inner.In = value; }
    public IMessage? Out { get => inner.Out; set => inner.Out = value; }
    public ExchangePattern Pattern { get => inner.Pattern; set => inner.Pattern = value; }
    public IDictionary<string, object?> Properties => inner.Properties;
    public Exception? Exception { get => inner.Exception; set => inner.Exception = value; }
    public bool ExceptionHandled { get => inner.ExceptionHandled; set => inner.ExceptionHandled = value; }
    public string? RouteId { get => inner.RouteId; set => inner.RouteId = value; }
    public IRouteContext? Context => ((IExchange)inner).Context;
    public IServiceProvider? ServiceProvider => ((IExchange)inner).ServiceProvider;
    public string ExchangeId => inner.ExchangeId;
    public bool IsStopped => inner.IsStopped;

    public T? GetProperty<T>(string key) => inner.GetProperty<T>(key);
    public void Stop() => inner.Stop();
    public IExchange Clone() => inner.Clone();
    public IExchange Snapshot() => inner.Snapshot();

    public IExchange CreateLinkedChild(IMessage message)
    {
        LinkedChildren++;
        return inner.CreateLinkedChild(message);
    }

    public ValueTask DisposeAsync() => ((IAsyncDisposable)inner).DisposeAsync();
}
