using Xunit.Sdk;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// xUnit fact attribute that skips the test when the named environment variable is
/// missing or empty. Used to gate live LLM provider tests so CI runs stay green
/// without secrets while local runs with the variable set hit the real API.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class EnvFactAttribute : FactAttribute
{
    /// <summary>Creates an env-gated fact.</summary>
    /// <param name="envVar">Required environment variable. If unset/empty the test is skipped.</param>
    /// <param name="extraEnvVars">Additional variables that must also be present.</param>
    public EnvFactAttribute(string envVar, params string[] extraEnvVars)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)))
            missing.Add(envVar);
        foreach (var v in extraEnvVars)
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                missing.Add(v);

        if (missing.Count > 0)
            Skip = $"Live LLM test skipped — set env vars: {string.Join(", ", missing)}.";
    }
}
