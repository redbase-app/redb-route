using System.Text;
using AwesomeAssertions;
using redb.Route.DataFormats.XmlJson;
using redb.Route.DataFormats.XmlJson.Internal;

namespace redb.Route.Tests.DataFormats;

/// <summary>
/// The plural-wrapper rule of xmljson (step S4): opt-in, regular and irregular English plurals, silent on every other
/// name, and a single child under a plural wrapper is an array.
/// </summary>
public sealed class XmlJsonPluralTests
{
    private static string Json(string xml, bool plural = true)
    {
        var converter = new XmlToJson(new XmlJsonOptions { Plural = plural });
        return Encoding.UTF8.GetString(converter.Write(converter.Convert(Encoding.UTF8.GetBytes(xml))));
    }

    [Theory]
    [InlineData("Incidents", "Incident")]
    [InlineData("Categories", "Category")]
    [InlineData("Boxes", "Box")]
    [InlineData("Addresses", "Address")]
    [InlineData("Statuses", "Status")]
    [InlineData("Cases", "Case")]
    [InlineData("Movies", "Movie")]
    [InlineData("Children", "Child")]
    [InlineData("people", "person")]
    [InlineData("Indices", "Index")]
    public void ThePairs(string parent, string child)
    {
        Plural.IsPair(parent, child).Should().BeTrue();
    }

    [Theory]
    [InlineData("Items", "Line")]
    [InlineData("Address", "Addres")]
    [InlineData("Status", "Code")]
    [InlineData("Data", "Datum")]
    [InlineData("incidents", "Incident")]
    [InlineData("Order", "Orde")]
    public void NotPairs(string parent, string child)
    {
        Plural.IsPair(parent, child).Should().BeFalse();
    }

    [Fact]
    public void ASingleChildOfAPluralWrapper_IsAnArray()
    {
        Json("<r><Incidents><Incident>1</Incident></Incidents></r>").Should().Be("""{"r":{"Incidents":{"Incident":["1"]}}}""");
    }

    [Fact]
    public void TheWrappersAttributes_DoNotMatter()
    {
        Json("""<Incidents count="1"><Incident>1</Incident></Incidents>""").Should().Be("""{"Incidents":{"@count":"1","Incident":["1"]}}""");
    }

    [Fact]
    public void AWrapperWithOtherChildren_IsNotAPluralWrapper()
    {
        Json("<Incidents><Incident>1</Incident><Total>1</Total></Incidents>").Should().Be("""{"Incidents":{"Incident":"1","Total":"1"}}""");
    }

    [Fact]
    public void OffByDefault()
    {
        Json("<Incidents><Incident>1</Incident></Incidents>", plural: false).Should().Be("""{"Incidents":{"Incident":"1"}}""");
        new XmlJsonOptions().Plural.Should().BeFalse();
    }

    [Fact]
    public void SilentOnOtherNames()
    {
        Json("<Order><Line>1</Line></Order>").Should().Be("""{"Order":{"Line":"1"}}""");
    }
}
