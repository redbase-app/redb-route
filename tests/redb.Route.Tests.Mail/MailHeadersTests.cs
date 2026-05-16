using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

public sealed class MailHeadersTests
{
    [Fact]
    public void AllHeaders_HaveCorrectPrefix()
    {
        var headerFields = typeof(MailHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name != nameof(MailHeaders.Prefix))
            .ToList();

        headerFields.Should().NotBeEmpty();

        foreach (var field in headerFields)
        {
            var val = (string)field.GetValue(null)!;
            val.Should().StartWith(MailHeaders.Prefix,
                $"header {field.Name} should start with '{MailHeaders.Prefix}'");
        }
    }

    [Fact]
    public void Prefix_IsCorrect()
    {
        MailHeaders.Prefix.Should().Be("redbMail.");
    }

    [Fact]
    public void IsRedbHeader_MatchingKey_ReturnsTrue()
    {
        MailHeaders.IsRedbHeader("redbMail.From").Should().BeTrue();
        MailHeaders.IsRedbHeader("redbMail.Custom").Should().BeTrue();
    }

    [Fact]
    public void IsRedbHeader_NonMatchingKey_ReturnsFalse()
    {
        MailHeaders.IsRedbHeader("Content-Type").Should().BeFalse();
        MailHeaders.IsRedbHeader("x-custom").Should().BeFalse();
        MailHeaders.IsRedbHeader("").Should().BeFalse();
    }

    [Fact]
    public void AllHeaders_AreUnique()
    {
        var headerFields = typeof(MailHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        headerFields.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void KnownHeaders_ExistAndCorrect()
    {
        MailHeaders.From.Should().Be("redbMail.From");
        MailHeaders.To.Should().Be("redbMail.To");
        MailHeaders.Cc.Should().Be("redbMail.Cc");
        MailHeaders.Bcc.Should().Be("redbMail.Bcc");
        MailHeaders.Subject.Should().Be("redbMail.Subject");
        MailHeaders.MessageId.Should().Be("redbMail.MessageId");
        MailHeaders.ContentType.Should().Be("redbMail.ContentType");
        MailHeaders.IsHtml.Should().Be("redbMail.IsHtml");
        MailHeaders.AttachmentCount.Should().Be("redbMail.AttachmentCount");
        MailHeaders.Uid.Should().Be("redbMail.Uid");
        MailHeaders.Folder.Should().Be("redbMail.Folder");
        MailHeaders.Protocol.Should().Be("redbMail.Protocol");
        MailHeaders.Priority.Should().Be("redbMail.Priority");
    }
}
