using redb.Route.Core;
using redb.Route.Ftp;

namespace redb.Route.Tests.Ftp;

/// <summary>
/// Ф11 волна Ж (Ж-1): a set-but-unknown <c>connectionFactory</c> name must fail loud —
/// never silently fall back to URI parameters.
/// </summary>
public sealed class FtpConnectionFactoryLoudTests
{
    [Fact]
    public async Task ConnectionFactoryTypo_FailsLoud()
    {
        await using var ctx = new RouteContext();
        ctx.AddComponent(new FtpComponent());

        var act = () => ctx.GetEndpoint("ftp://host/inbox?connectionFactory=typo-name&username=u&password=p");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("typo-name");
    }
}
