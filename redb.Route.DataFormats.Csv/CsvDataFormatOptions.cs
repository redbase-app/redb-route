using System.Globalization;
using System.Text;

namespace redb.Route.DataFormats.Csv;

/// <summary>Options of one <see cref="CsvDataFormat"/> instance (per node or per registration).</summary>
public sealed class CsvDataFormatOptions
{
    /// <summary>Field delimiter. Default <c>,</c>.</summary>
    public string Delimiter { get; set; } = ",";

    /// <summary>Whether the first record is a header (written on marshal, read on unmarshal). Default <c>true</c>.</summary>
    public bool HasHeaderRecord { get; set; } = true;

    /// <summary>Quote character. Default <c>"</c>.</summary>
    public char Quote { get; set; } = '"';

    /// <summary>Culture for number and date conversion. Default invariant.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>Trim whitespace around fields on read. Default <c>false</c>.</summary>
    public bool TrimFields { get; set; }

    /// <summary>Record terminator written on marshal. Default <c>\r\n</c> (RFC 4180).</summary>
    public string NewLine { get; set; } = "\r\n";

    /// <summary>Text encoding. Default UTF-8 without BOM.</summary>
    public Encoding Encoding { get; set; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
