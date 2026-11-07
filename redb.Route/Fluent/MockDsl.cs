using System.Text;
using redb.Route.Abstractions;

namespace redb.Route;

/// <summary>
/// Fluent entry point for Mock endpoints (testing).
/// <example><code>
/// .To(MockDsl.Endpoint("result").ExpectedMessageCount(3))
/// </code></example>
/// </summary>
public static class MockDsl
{
    /// <summary>Create a named mock endpoint.</summary>
    public static MockBuilder Endpoint(string name) => new(name);
}

/// <summary>Fluent builder for Mock endpoint URIs.</summary>
public sealed class MockBuilder
{
    private readonly string _name;
    private string? _expectedMessageCount;

    internal MockBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>Expected number of messages to receive.</summary>
    public MockBuilder ExpectedMessageCount(int count) { _expectedMessageCount = count.ToString(); return this; }
    /// <summary>Expected message count from an expression.</summary>
    public MockBuilder ExpectedMessageCount(IExpression count) { _expectedMessageCount = count.ToTemplateString(); return this; }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("mock:");
        sb.Append(_name);

        if (_expectedMessageCount != null)
        {
            sb.Append("?expectedMessageCount=");
            sb.Append(_expectedMessageCount);
        }

        return sb.ToString();
    }

    public static implicit operator string(MockBuilder b) => b.Build();
    public override string ToString() => Build();
}
