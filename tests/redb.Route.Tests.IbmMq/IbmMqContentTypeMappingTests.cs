using IBM.WMQ;
using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// Tests for <see cref="IbmMqMessageHelper.FormatToContentType"/> and
/// <see cref="IbmMqMessageHelper.ContentTypeToFormat"/> mappings.
/// </summary>
public sealed class IbmMqContentTypeMappingTests
{
    [Theory]
    [InlineData("MQSTR   ", "text/plain")]         // MQFMT_STRING is padded
    [InlineData("MQHRF2  ", "text/plain")]         // MQFMT_RF_HEADER_2
    [InlineData("        ", "application/octet-stream")] // MQFMT_NONE (spaces)
    public void FormatToContentType_MapsKnownFormats(string format, string expected)
    {
        IbmMqMessageHelper.FormatToContentType(format).Should().Be(expected);
    }

    [Fact]
    public void FormatToContentType_Null_ReturnsNull()
    {
        IbmMqMessageHelper.FormatToContentType(null).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatToContentType_EmptyOrWhitespace_ReturnsBinary(string format)
    {
        // Empty / whitespace-only = MQFMT_NONE semantics → binary
        IbmMqMessageHelper.FormatToContentType(format).Should().Be("application/octet-stream");
    }

    [Theory]
    [InlineData("MQCICS  ")]
    [InlineData("MQIMS   ")]
    public void FormatToContentType_UnknownFormat_ReturnsNull(string format)
    {
        IbmMqMessageHelper.FormatToContentType(format).Should().BeNull();
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("text/xml")]
    [InlineData(null)]
    public void ContentTypeToFormat_TextTypes_ReturnsMqfmtString(string? contentType)
    {
        var fmt = IbmMqMessageHelper.ContentTypeToFormat(contentType);
        fmt.Trim().Should().Be(MQC.MQFMT_STRING.Trim());
    }

    [Fact]
    public void ContentTypeToFormat_OctetStream_ReturnsMqfmtNone()
    {
        var fmt = IbmMqMessageHelper.ContentTypeToFormat("application/octet-stream");
        fmt.Trim().Should().Be(MQC.MQFMT_NONE.Trim());
    }

    [Fact]
    public void RoundTrip_StringFormat()
    {
        var ct = IbmMqMessageHelper.FormatToContentType(MQC.MQFMT_STRING);
        ct.Should().Be("text/plain");
        var fmt = IbmMqMessageHelper.ContentTypeToFormat(ct);
        fmt.Trim().Should().Be(MQC.MQFMT_STRING.Trim());
    }

    [Fact]
    public void RoundTrip_NoneFormat()
    {
        var ct = IbmMqMessageHelper.FormatToContentType(MQC.MQFMT_NONE);
        ct.Should().Be("application/octet-stream");
        var fmt = IbmMqMessageHelper.ContentTypeToFormat(ct);
        fmt.Trim().Should().Be(MQC.MQFMT_NONE.Trim());
    }
}
