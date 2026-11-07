# redb.Route.DataFormats.Yaml

YAML as a redb.Route data format, on [YamlDotNet](https://github.com/aaubry/YamlDotNet).

```csharp
.UnmarshalYaml<Deployment>()                         // string / byte[] / Stream → POCO
.UnmarshalYaml<object>()                             // → Dictionary<string, object?> / List<object?> tree
.MarshalYaml(o => o.NamingConvention = PascalCaseNamingConvention.Instance)

context.AddYamlDataFormat();                         // then Marshal("application/yaml"), Unmarshal<T>("application/yaml"),
                                                     // and ContentType-driven Unmarshal<T>() for application/x-yaml, text/yaml
```

Options: `NamingConvention` (camelCase by default, like the JSON codec), `IgnoreUnmatchedProperties` (`true`).
Malformed YAML fails with an error naming the format.
