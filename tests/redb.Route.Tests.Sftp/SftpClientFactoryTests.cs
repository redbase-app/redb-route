using redb.Route.Sftp;

namespace redb.Route.Tests.Sftp;

public sealed class SftpClientFactoryTests
{
    [Fact]
    public void Create_NullOptions_Throws()
    {
        var act = () => SftpClientFactory.Create(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_PasswordAuth_ReturnsClient()
    {
        var options = new SftpEndpointOptions
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            Password = "secret"
        };

        using var client = SftpClientFactory.Create(options);

        client.Should().NotBeNull();
        client.OperationTimeout.Should().Be(TimeSpan.FromMilliseconds(60_000));
        client.BufferSize.Should().Be(32_768u);
    }

    [Fact]
    public void Create_CustomTimeouts_Applied()
    {
        var options = new SftpEndpointOptions
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            Password = "secret",
            OperationTimeout = 5000,
            BufferSize = 65536
        };

        using var client = SftpClientFactory.Create(options);

        client.OperationTimeout.Should().Be(TimeSpan.FromMilliseconds(5000));
        client.BufferSize.Should().Be(65536u);
    }

    [Fact]
    public void Create_KeepAlive_Applied()
    {
        var options = new SftpEndpointOptions
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            Password = "secret",
            KeepAliveInterval = 10000
        };

        using var client = SftpClientFactory.Create(options);

        client.KeepAliveInterval.Should().Be(TimeSpan.FromMilliseconds(10000));
    }

    [Fact]
    public void Create_NoAuthMethods_Throws()
    {
        var options = new SftpEndpointOptions
        {
            Host = "localhost",
            Username = "testuser",
            Password = "",
            PrivateKeyPath = ""
        };

        var act = () => SftpClientFactory.Create(options);
        act.Should().Throw<InvalidOperationException>().WithMessage("*authentication*");
    }

    [Fact]
    public void Create_InvalidPrivateKeyPath_Throws()
    {
        var options = new SftpEndpointOptions
        {
            Host = "localhost",
            Username = "testuser",
            PrivateKeyPath = "/nonexistent/path/key"
        };

        // SSH.NET will throw when trying to load the key file
        var act = () => SftpClientFactory.Create(options);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Create_StrictHostKeyWithFingerprint_NoThrow()
    {
        var options = new SftpEndpointOptions
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            Password = "secret",
            StrictHostKeyChecking = true,
            ServerFingerprint = "AA:BB:CC:DD"
        };

        // Should create client (fingerprint validated on connect, not create)
        using var client = SftpClientFactory.Create(options);
        client.Should().NotBeNull();
    }

    [Fact]
    public void Create_NonStrictHostKey_NoThrow()
    {
        var options = new SftpEndpointOptions
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            Password = "secret",
            StrictHostKeyChecking = false
        };

        using var client = SftpClientFactory.Create(options);
        client.Should().NotBeNull();
    }

    [Fact]
    public void Create_WithProxy_ReturnsClient()
    {
        var options = new SftpEndpointOptions
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            Password = "secret",
            ProxyType = SftpProxyType.Socks5,
            ProxyHost = "proxy.local",
            ProxyPort = 1080,
            ProxyUsername = "proxyuser",
            ProxyPassword = "proxypass"
        };

        using var client = SftpClientFactory.Create(options);
        client.Should().NotBeNull();
    }

    [Fact]
    public void Create_WithKeyboardInteractive_ReturnsClient()
    {
        var options = new SftpEndpointOptions
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            Password = "secret",
            UseKeyboardInteractive = true
        };

        using var client = SftpClientFactory.Create(options);
        client.Should().NotBeNull();
    }
}
