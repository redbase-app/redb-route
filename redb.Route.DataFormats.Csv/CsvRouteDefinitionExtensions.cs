using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.DataFormats.Csv;

/// <summary>DSL and registration sugar for <see cref="CsvDataFormat"/>.</summary>
public static class CsvRouteDefinitionExtensions
{
    /// <summary>Marshals the body to CSV with per-node options.</summary>
    public static IRouteDefinition MarshalCsv(this IRouteDefinition route, Action<CsvDataFormatOptions>? configure = null)
        => route.Marshal(new CsvDataFormat(Build(configure)));

    /// <summary>Unmarshals CSV to <typeparamref name="T"/> (<c>List&lt;Row&gt;</c>, <c>Row[]</c>, <c>List&lt;Dictionary&lt;string,string&gt;&gt;</c>, <c>List&lt;string[]&gt;</c>) with per-node options.</summary>
    public static IRouteDefinition UnmarshalCsv<T>(this IRouteDefinition route, Action<CsvDataFormatOptions>? configure = null)
        => route.Unmarshal<T>(new CsvDataFormat(Build(configure)));

    /// <summary>Registers CSV as <c>text/csv</c> on the context, for <c>Marshal("text/csv")</c>, <c>Unmarshal&lt;T&gt;("text/csv")</c> and content-type-driven <c>Unmarshal&lt;T&gt;()</c>.</summary>
    public static IRouteContext AddCsvDataFormat(this IRouteContext context, Action<CsvDataFormatOptions>? configure = null)
        => context.AddDataFormat(new CsvDataFormat(Build(configure)));

    /// <summary>DI form of <see cref="AddCsvDataFormat(IRouteContext, Action{CsvDataFormatOptions})"/>.</summary>
    public static RedbRouteBuilder AddCsvDataFormat(this RedbRouteBuilder builder, Action<CsvDataFormatOptions>? configure = null)
        => builder.AddDataFormat(new CsvDataFormat(Build(configure)));

    private static CsvDataFormatOptions Build(Action<CsvDataFormatOptions>? configure)
    {
        var options = new CsvDataFormatOptions();
        configure?.Invoke(options);
        return options;
    }
}
