using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

public sealed class MailEnumsTests
{
    [Fact]
    public void MailSecurityMode_HasExpectedValues()
    {
        Enum.GetNames<MailSecurityMode>().Should()
            .BeEquivalentTo("None", "Ssl", "StartTls", "Auto");
    }

    [Fact]
    public void MailAuthMechanism_HasExpectedValues()
    {
        Enum.GetNames<MailAuthMechanism>().Should().Contain(new[]
        {
            "Auto", "Plain", "Login", "CramMd5", "XOAuth2", "OAuthBearer", "Ntlm"
        });
    }

    [Fact]
    public void PostProcessAction_HasExpectedValues()
    {
        Enum.GetNames<PostProcessAction>().Should().Contain(new[]
        {
            "None", "MarkRead", "Delete", "Move", "MarkReadAndMove", "Flag"
        });
    }

    [Fact]
    public void MailSortBy_HasExpectedValues()
    {
        Enum.GetNames<MailSortBy>().Should().Contain(new[]
        {
            "None", "DateAsc", "DateDesc", "SubjectAsc", "FromAsc", "SizeAsc", "SizeDesc"
        });
    }

    [Fact]
    public void MailFetchFilter_HasExpectedValues()
    {
        Enum.GetNames<MailFetchFilter>().Should().Contain(new[]
        {
            "Unseen", "All", "Recent", "Flagged", "Answered", "Unanswered"
        });
    }
}
