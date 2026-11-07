namespace redb.Route;

/// <summary>
/// Fluent entry point for Direct (synchronous in-process) endpoints.
/// <example><code>
/// .From(Direct.Endpoint("input"))
/// .To(Direct.Endpoint("processing"))
/// </code></example>
/// </summary>
public static class Direct
{
    /// <summary>Reference a named direct endpoint (no configuration parameters).</summary>
    public static DirectBuilder Endpoint(string name) => new(name);
}

/// <summary>Fluent builder for Direct endpoint URIs.</summary>
public sealed class DirectBuilder
{
    private readonly string _name;

    internal DirectBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    public string Build() => $"direct:{_name}";

    public static implicit operator string(DirectBuilder b) => b.Build();
    public override string ToString() => Build();
}
