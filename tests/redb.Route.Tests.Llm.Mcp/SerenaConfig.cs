using System.IO;
using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace redb.Route.Tests.Llm.Mcp;

/// <summary>
/// Resolves Serena's launch configuration from <c>.mcp.json</c> at the repo root.
/// Tests use <see cref="SerenaFactAttribute"/> to gate on its availability.
/// </summary>
internal static class SerenaConfig
{
    private static readonly Lazy<SerenaLaunch?> _launch = new(Resolve);

    public static SerenaLaunch? Launch => _launch.Value;

    public static bool IsAvailable => Launch is not null;

    public static string SkipReason =>
        "Serena MCP launch not found — expected '.mcp.json' with a 'Serena' entry at the repo root, or env var SERENA_MCP_LAUNCH.";

    private static SerenaLaunch? Resolve()
    {
        // Env override: SERENA_MCP_LAUNCH=command|arg1|arg2|...
        var envValue = Environment.GetEnvironmentVariable("SERENA_MCP_LAUNCH");
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            var parts = envValue.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                return new SerenaLaunch(parts[0], parts.Skip(1).ToArray());
        }

        // Climb up from BaseDirectory until we find .mcp.json.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, ".mcp.json");
            if (File.Exists(candidate))
                return ParseFile(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static SerenaLaunch? ParseFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var root = JsonNode.Parse(json);
            var servers = root?["servers"];
            if (servers is null) return null;

            // Match case-insensitively — `.mcp.json` typically uses "Serena".
            JsonNode? entry = null;
            foreach (var kv in servers.AsObject())
            {
                if (string.Equals(kv.Key, "serena", StringComparison.OrdinalIgnoreCase))
                {
                    entry = kv.Value;
                    break;
                }
            }
            if (entry is null) return null;

            var command = entry["command"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(command)) return null;

            var args = entry["args"]?.AsArray()
                .Select(n => n?.GetValue<string>() ?? "")
                .Where(s => s.Length > 0)
                .ToArray() ?? [];

            return new SerenaLaunch(command, args);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Materialised Serena launch description.</summary>
internal sealed record SerenaLaunch(string Command, IReadOnlyList<string> Arguments);

/// <summary>
/// xUnit fact gated on Serena's availability via <see cref="SerenaConfig.IsAvailable"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class SerenaFactAttribute : FactAttribute
{
    /// <summary>Creates a Serena-gated fact.</summary>
    public SerenaFactAttribute()
    {
        if (!SerenaConfig.IsAvailable)
            Skip = SerenaConfig.SkipReason;
    }
}

/// <summary>
/// xUnit fact gated on BOTH Serena availability and the named environment variable(s).
/// Skips with a clear reason when either side is missing.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class SerenaEnvFactAttribute : FactAttribute
{
    /// <summary>Creates a Serena+env-gated fact.</summary>
    /// <param name="envVar">Required environment variable.</param>
    /// <param name="extraEnvVars">Additional variables that must also be present.</param>
    public SerenaEnvFactAttribute(string envVar, params string[] extraEnvVars)
    {
        if (!SerenaConfig.IsAvailable)
        {
            Skip = SerenaConfig.SkipReason;
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)))
            missing.Add(envVar);
        foreach (var v in extraEnvVars)
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                missing.Add(v);

        if (missing.Count > 0)
            Skip = $"Live LLM+MCP test skipped — set env vars: {string.Join(", ", missing)}.";
    }
}
