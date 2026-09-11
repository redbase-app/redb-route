using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>Code review 2026-09-01: a mask is validated where it is declared, so an invalid regex names itself instead of dropping a route at compile.</summary>
public class UriMaskValidateTests
{
    [Fact]
    public void InvalidRegex_IsReportedAsSuch()
    {
        var act = () => UriMask.Validate("regex:[", "mask");

        act.Should().Throw<ArgumentException>().WithMessage("*regular expression*").And.ParamName.Should().Be("mask");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankMask_IsRefused(string? pattern)
    {
        var act = () => UriMask.Validate(pattern);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("kafka://*")]
    [InlineData("regex:^kafka:.*$")]
    [InlineData("X-Internal-*")]
    public void UsableMasks_Pass(string pattern)
    {
        var act = () => UriMask.Validate(pattern);

        act.Should().NotThrow();
    }
}
