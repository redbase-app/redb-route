using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

public sealed class LdapOperationTypeTests
{
    [Theory]
    [InlineData("SEARCH", LdapOperationType.SEARCH)]
    [InlineData("COMPARE", LdapOperationType.COMPARE)]
    [InlineData("ADD", LdapOperationType.ADD)]
    [InlineData("MODIFY", LdapOperationType.MODIFY)]
    [InlineData("DELETE", LdapOperationType.DELETE)]
    [InlineData("RENAME", LdapOperationType.RENAME)]
    [InlineData("BIND", LdapOperationType.BIND)]
    [InlineData("WATCH", LdapOperationType.WATCH)]
    public void AllOperationTypes_CanBeParsed(string name, LdapOperationType expected)
    {
        Enum.TryParse<LdapOperationType>(name, ignoreCase: true, out var result).Should().BeTrue();
        result.Should().Be(expected);
    }

    [Fact]
    public void TotalOperationCount_Is8()
    {
        Enum.GetValues<LdapOperationType>().Length.Should().Be(8);
    }
}
