using System.Text;

namespace redb.Route;

/// <summary>
/// Fluent entry point for Log producer endpoints.
/// <example><code>
/// .To(LogDsl.Info("myLogger").ShowHeaders().ShowBody())
/// .To(LogDsl.Debug("trace"))
/// </code></example>
/// </summary>
public static class LogDsl
{
    public static LogBuilder Trace(string name) => new(name, "Trace");
    public static LogBuilder Debug(string name) => new(name, "Debug");
    public static LogBuilder Info(string name) => new(name, "Information");
    public static LogBuilder Warn(string name) => new(name, "Warning");
    public static LogBuilder Error(string name) => new(name, "Error");
}

/// <summary>Fluent builder for Log endpoint URIs.</summary>
public sealed class LogBuilder
{
    private readonly string _name;
    private readonly string _level;
    private bool _showHeaders;
    private bool _showBody;

    internal LogBuilder(string name, string level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _level = level;
    }

    /// <summary>Include message headers in log output.</summary>
    public LogBuilder ShowHeaders() { _showHeaders = true; return this; }

    /// <summary>Include message body in log output.</summary>
    public LogBuilder ShowBody() { _showBody = true; return this; }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("log:");
        sb.Append(_name);

        var sep = '?';
        void Append(string key, string v) { sb.Append(sep); sb.Append(key); sb.Append('='); sb.Append(v); sep = '&'; }
        void AppendBool(string key, bool v) { if (v) Append(key, "true"); }

        Append("level", _level);
        AppendBool("showHeaders", _showHeaders);
        AppendBool("showBody", _showBody);

        return sb.ToString();
    }

    public static implicit operator string(LogBuilder b) => b.Build();
    public override string ToString() => Build();
}
