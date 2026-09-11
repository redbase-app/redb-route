using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace redb.Route.DataFormats.Yaml;

/// <summary>Options of one <see cref="YamlDataFormat"/> instance.</summary>
public sealed class YamlDataFormatOptions
{
    /// <summary>Property naming in the document. Default camelCase (the same choice as the JSON codec).</summary>
    public INamingConvention NamingConvention { get; set; } = CamelCaseNamingConvention.Instance;

    /// <summary>Ignore document keys that have no matching property on unmarshal. Default <c>true</c>.</summary>
    public bool IgnoreUnmatchedProperties { get; set; } = true;
}
