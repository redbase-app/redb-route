using System.IO;
using System.Runtime.CompilerServices;

namespace redb.Route.Tests.Llm.Mcp.TestHelpers;

/// <summary>
/// Loads <c>.env.local</c> from <c>redb.Tsak/publish/keys/.env.local</c> into the
/// process environment before any test runs. Mirrors the loader used by
/// <c>redb.Route.Tests.Llm</c> — duplicated here because <see cref="ModuleInitializerAttribute"/>
/// is per-assembly. Existing environment variables are never overwritten.
/// </summary>
internal static class EnvLoader
{
    [ModuleInitializer]
    internal static void Load()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "redb.Tsak", "publish", "keys", ".env.local");
            if (File.Exists(candidate))
            {
                Apply(candidate);
                return;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
    }

    private static void Apply(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                continue;

            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
