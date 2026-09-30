using System.Text.RegularExpressions;
using FluentAssertions;

namespace redb.Route.Tests.Core;

/// <summary>
/// A connection factory holds the credentials a route must not write into its URI, and <c>[Sensitive]</c> is the one
/// declaration that tells every renderer to mask them. No test project references every connector, so the guard reads
/// the factories' sources: a public property whose name says it is a secret carries the attribute.
/// </summary>
public class ConnectionFactorySecretsTests
{
    private static readonly Regex SecretProperty = new(
        @"^\s*public\s+(?:required\s+)?[\w<>?.\[\]]+\s+(\w*(?:Password|Passphrase|Secret|SecretKey|Token|ApiKey|AccessKey|ConnectionString))\s*\{",
        RegexOptions.Multiline);

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "redb.Route.slnx")))
                return Path.Combine(dir.FullName, "src");
        throw new InvalidOperationException("redb.Route.slnx not found above the test output directory.");
    }

    [Fact]
    public void Every_secret_of_a_connection_factory_is_marked_sensitive()
    {
        var unmarked = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*ConnectionFactory.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = SecretProperty.Match(lines[i]);
                if (!match.Success)
                    continue;
                var previous = lines.Take(i).LastOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("///")) ?? "";
                if (!lines[i].Contains("Sensitive]") && !previous.Contains("Sensitive]"))
                    unmarked.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
            }
        }

        unmarked.Should().BeEmpty("a credential on a connection factory is masked by declaration, not by luck");
    }
}
