using System.Runtime.CompilerServices;

namespace redb.Route.Demo;

/// <summary>
/// Loads <c>redb.Tsak/publish/keys/.env.local</c> into <see cref="Environment"/>
/// on first assembly use, so the LLM demo routes find their API keys without
/// the operator having to export them by hand. Existing environment variables
/// are never overwritten — production containers that set keys via
/// <c>environment:</c> in compose continue to win.
/// <para>
/// Missing file = silent no-op (CI / fresh checkout). The lookup climbs from
/// <c>AppContext.BaseDirectory</c> upwards looking for a sibling
/// <c>redb.Tsak/publish/keys/.env.local</c>, matching the layout this repo uses.
/// </para>
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
            if (System.IO.File.Exists(candidate))
            {
                Apply(candidate);
                return;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
    }

    private static void Apply(string path)
    {
        foreach (var rawLine in System.IO.File.ReadAllLines(path))
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
