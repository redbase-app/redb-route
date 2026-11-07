using System.Text;
using redb.Route.Abstractions;

namespace redb.Route;

/// <summary>
/// Fluent entry point for SEDA (Staged Event-Driven Architecture) in-memory queue endpoints.
/// <example><code>
/// .From(Seda.Consume("orders").ConcurrentConsumers(5).Size(1000))
/// .To(Seda.Send("orders"))
/// </code></example>
/// </summary>
public static class Seda
{
    /// <summary>Consume from a named SEDA queue.</summary>
    public static SedaBuilder Consume(string name) => new(name);

    /// <summary>Send to a named SEDA queue.</summary>
    public static SedaBuilder Send(string name) => new(name);
}

/// <summary>Fluent builder for SEDA endpoint URIs.</summary>
public sealed class SedaBuilder
{
    private readonly string _name;
    private string? _concurrentConsumers;
    private string? _size;
    private string? _timeout;

    internal SedaBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>Number of concurrent consumer threads. Default 1.</summary>
    public SedaBuilder ConcurrentConsumers(int count) { _concurrentConsumers = count.ToString(); return this; }
    /// <summary>Concurrent consumers from an expression.</summary>
    public SedaBuilder ConcurrentConsumers(IExpression count) { _concurrentConsumers = count.ToTemplateString(); return this; }

    /// <summary>Maximum queue capacity. Default unbounded.</summary>
    public SedaBuilder Size(int size) { _size = size.ToString(); return this; }
    /// <summary>Queue size from an expression.</summary>
    public SedaBuilder Size(IExpression size) { _size = size.ToTemplateString(); return this; }

    /// <summary>Timeout in ms when queue is full. Default infinite.</summary>
    public SedaBuilder Timeout(int ms) { _timeout = ms.ToString(); return this; }
    /// <summary>Timeout from an expression.</summary>
    public SedaBuilder Timeout(IExpression ms) { _timeout = ms.ToTemplateString(); return this; }

    /// <summary>Builds the SEDA URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("seda:");
        sb.Append(_name);

        var sep = '?';
        void AppendIf(string key, string? v) { if (v != null) { sb.Append(sep); sb.Append(key); sb.Append('='); sb.Append(v); sep = '&'; } }

        AppendIf("concurrentConsumers", _concurrentConsumers);
        AppendIf("size", _size);
        AppendIf("timeout", _timeout);

        return sb.ToString();
    }

    public static implicit operator string(SedaBuilder b) => b.Build();
    public override string ToString() => Build();
}
