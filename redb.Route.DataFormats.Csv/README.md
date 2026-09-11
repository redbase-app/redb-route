# redb.Route.DataFormats.Csv

CSV as a redb.Route data format, on [CsvHelper](https://joshclose.github.io/CsvHelper/).

```csharp
// per-node options
.UnmarshalCsv<List<OrderRow>>(o => { o.Delimiter = ";"; o.HasHeaderRecord = true; })
.MarshalCsv()

// or by content type, after registering once
context.AddCsvDataFormat();                 // builder.AddCsvDataFormat() with DI
.Marshal("text/csv")
.Unmarshal<List<OrderRow>>("text/csv")
.Unmarshal<List<OrderRow>>()                // driven by the message ContentType (text/csv)
```

| Direction | Body in | Body out |
|---|---|---|
| Marshal | `IEnumerable<T>` of POCOs (header from property names), `IEnumerable<IDictionary>` (header from keys), `IEnumerable<string[]>` / `object[]` rows (no header), a single POCO, a ready CSV `string` | `byte[]`, `ContentType = text/csv` |
| Unmarshal | `byte[]`, `string`, `Stream` | `List<T>` / `T[]` / `IEnumerable<T>` of POCOs; `List<Dictionary<string,string>>` (with header) or `List<string[]>` (without); a single `T`; `string` |

Options: `Delimiter` (`,`), `HasHeaderRecord` (`true`), `Quote` (`"`), `Culture` (invariant), `TrimFields`,
`NewLine` (`\r\n`), `Encoding` (UTF-8 without BOM). Quotes, embedded newlines and empty fields follow RFC 4180.

XML form: `<unmarshal format="text/csv" delimiter=";" header="true" type="Acme.OrderRow, Acme"/>`, `<marshal format="text/csv"/>`.
