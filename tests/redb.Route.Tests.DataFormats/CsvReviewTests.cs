using System.Text;
using redb.Route.DataFormats.Csv;

namespace redb.Route.Tests.DataFormats;

/// <summary>Code review 2026-09-01, fourth batch: the header line is not a data row, and the header of dictionary rows is the union of their keys.</summary>
public class CsvReviewTests
{
    [Fact]
    public void RawRows_SkipTheHeaderLine_WhenAHeaderIsConfigured()
    {
        var rows = new CsvDataFormat().Deserialize<List<string[]>>("a,b\n1,2\n3,4\n"u8.ToArray())!;

        rows.Should().HaveCount(2);
        rows[0].Should().Equal("1", "2");
        rows[1].Should().Equal("3", "4");
    }

    [Fact]
    public void DictionaryRows_HeaderIsTheUnionOfEveryRowsKeys()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["a"] = 1, ["b"] = 2 },
            new() { ["a"] = 3, ["b"] = 4, ["c"] = 5 },
        };

        var text = Encoding.UTF8.GetString(new CsvDataFormat().Serialize(rows));

        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Equal("a,b,c", "1,2,", "3,4,5");
    }
}
