using Microsoft.Extensions.Configuration;
using redb.Route.Abstractions;

namespace SerialNumbers.Worker;

/// <summary>
/// The context configuration the way the Tsak worker builds it, in miniature. Each layer deep-merges
/// over the previous one, later layers win:
/// <list type="number">
///   <item><c>Tsak:Contexts:default</c> of the host configuration,</item>
///   <item><c>Tsak:Contexts:{context}</c>,</item>
///   <item>the module's <c>{Module}.config.json</c>,</item>
///   <item><c>Tsak:Contexts:{context}:Override</c>, the operator's last word.</item>
/// </list>
/// Every root key then becomes a property of the route context; a section becomes a dictionary. The
/// real worker also reads a module's <c>context.json</c> and per-context <c>Contexts</c> blocks inside
/// the module files; this module uses neither.
/// </summary>
public static class ContextConfiguration
{
    /// <summary>The context a module asks for, from its <c>ContextName</c>.</summary>
    public static string ContextNameOf(string moduleConfigPath) =>
        new ConfigurationBuilder().AddJsonFile(moduleConfigPath, optional: false).Build()["ContextName"]
        ?? throw new InvalidOperationException($"{moduleConfigPath} has no ContextName.");

    public static IDictionary<string, object?> Build(IConfiguration host, string contextName, string moduleConfigPath)
    {
        var config = NewDictionary();
        Merge(config, Read(host.GetSection("Tsak:Contexts:default"), skipReserved: true));
        Merge(config, Read(host.GetSection($"Tsak:Contexts:{contextName}"), skipReserved: true));
        Merge(config, Read(new ConfigurationBuilder().AddJsonFile(moduleConfigPath, optional: false).Build(), skipReserved: false));
        Merge(config, Read(host.GetSection($"Tsak:Contexts:{contextName}:Override"), skipReserved: false));
        return config;
    }

    public static void ApplyTo(IRouteContext context, IDictionary<string, object?> config)
    {
        foreach (var (key, value) in config)
        {
            if (value is not null)
                context.SetProperty(key, value);
        }
    }

    private static Dictionary<string, object?> Read(IConfiguration section, bool skipReserved)
    {
        var result = NewDictionary();
        foreach (var child in section.GetChildren())
        {
            // A context section carries the module list and the Override layer next to the settings.
            if (skipReserved && (child.Key.Equals("Modules", StringComparison.OrdinalIgnoreCase)
                                 || child.Key.Equals("Override", StringComparison.OrdinalIgnoreCase)))
                continue;

            result[child.Key] = child.GetChildren().Any() ? Read(child, skipReserved: false) : child.Value;
        }
        return result;
    }

    private static void Merge(IDictionary<string, object?> target, IDictionary<string, object?> layer)
    {
        foreach (var (key, value) in layer)
        {
            if (value is IDictionary<string, object?> nested
                && target.TryGetValue(key, out var existing) && existing is IDictionary<string, object?> current)
                Merge(current, nested);
            else
                target[key] = value;
        }
    }

    private static Dictionary<string, object?> NewDictionary() => new(StringComparer.OrdinalIgnoreCase);
}
