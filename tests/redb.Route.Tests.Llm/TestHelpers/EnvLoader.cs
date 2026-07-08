using System.IO;
using System.Runtime.CompilerServices;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Loads <c>.env.local</c> from the well-known publish folder
/// (<c>redb.Tsak/publish/keys/.env.local</c>) into <see cref="Environment"/>
/// before any test runs. Existing environment variables are NEVER overwritten —
/// this is purely a developer convenience so a contributor with the file in
/// place can run the live suite without exporting variables manually.
/// <para>
/// Missing file = silent no-op. CI runs (without keys) skip every <c>[EnvFact]</c>
/// in the suite.
/// </para>
/// </summary>
internal static class EnvLoader
{
    [ModuleInitializer]
    internal static void Load()
    {
        // The repo layout is:
        //   <repo>/redb.Route/tests/redb.Route.Tests.Llm/bin/Debug/netX.0/...
        //   <repo>/redb.Tsak/publish/keys/.env.local
        // Climb until we hit a directory containing both subtrees.
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

            // Never clobber a value already in the environment.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                continue;

            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
