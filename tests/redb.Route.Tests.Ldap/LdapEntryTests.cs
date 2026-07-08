using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

public sealed class LdapEntryTests
{
    [Fact]
    public void Defaults_AreEmpty()
    {
        var entry = new LdapEntry();
        entry.Dn.Should().BeEmpty();
        entry.Attributes.Should().BeEmpty();
        entry.ChangeType.Should().BeNull();
    }

    [Fact]
    public void GetString_ExistingAttribute_ReturnsValue()
    {
        var entry = new LdapEntry
        {
            Dn = "cn=alice,dc=test",
            Attributes = { ["cn"] = "alice", ["mail"] = "alice@test.com" }
        };

        entry.GetString("cn").Should().Be("alice");
        entry.GetString("mail").Should().Be("alice@test.com");
    }

    [Fact]
    public void GetString_MissingAttribute_ReturnsNull()
    {
        var entry = new LdapEntry { Dn = "cn=alice,dc=test" };
        entry.GetString("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetStringArray_MultiValuedAttribute_ReturnsArray()
    {
        var values = new[] { "group1", "group2", "group3" };
        var entry = new LdapEntry
        {
            Dn = "cn=alice,dc=test",
            Attributes = { ["memberOf"] = values }
        };

        entry.GetStringArray("memberOf").Should().BeEquivalentTo(values);
    }

    [Fact]
    public void GetStringArray_SingleValuedAttribute_ReturnsNull()
    {
        var entry = new LdapEntry
        {
            Dn = "cn=alice,dc=test",
            Attributes = { ["cn"] = "alice" }
        };

        entry.GetStringArray("cn").Should().BeNull();
    }

    [Fact]
    public void GetBytes_BinaryAttribute_ReturnsBytes()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var entry = new LdapEntry
        {
            Dn = "cn=alice,dc=test",
            Attributes = { ["objectGUID"] = bytes }
        };

        entry.GetBytes("objectGUID").Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public void GetBytes_NonBinaryAttribute_ReturnsNull()
    {
        var entry = new LdapEntry
        {
            Dn = "cn=alice,dc=test",
            Attributes = { ["cn"] = "alice" }
        };

        entry.GetBytes("cn").Should().BeNull();
    }

    [Fact]
    public void GetBytes_MultiValuedBinary_ReturnsFirstElement()
    {
        var first = new byte[] { 0x01, 0x02 };
        var second = new byte[] { 0x03, 0x04 };
        var entry = new LdapEntry
        {
            Dn = "cn=alice,dc=test",
            Attributes = { ["certs"] = new[] { first, second } }
        };

        entry.GetBytes("certs").Should().BeEquivalentTo(first);
    }

    [Fact]
    public void GetBytesArray_MultiValuedBinary_ReturnsAll()
    {
        var first = new byte[] { 0x01, 0x02 };
        var second = new byte[] { 0x03, 0x04 };
        var multi = new[] { first, second };
        var entry = new LdapEntry
        {
            Dn = "cn=alice,dc=test",
            Attributes = { ["certs"] = multi }
        };

        entry.GetBytesArray("certs").Should().HaveCount(2);
    }

    [Fact]
    public void GetBytesArray_SingleBinary_ReturnsNull()
    {
        var entry = new LdapEntry
        {
            Dn = "cn=alice,dc=test",
            Attributes = { ["objectGUID"] = new byte[] { 0x01 } }
        };

        entry.GetBytesArray("objectGUID").Should().BeNull();
    }

    [Fact]
    public void ChangeType_CanBeSet()
    {
        var entry = new LdapEntry { ChangeType = "modified" };
        entry.ChangeType.Should().Be("modified");
    }
}
