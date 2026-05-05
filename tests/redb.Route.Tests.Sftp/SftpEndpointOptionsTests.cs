using redb.Route.GenericFile;
using redb.Route.Sftp;

namespace redb.Route.Tests.Sftp;

public sealed class SftpEndpointOptionsTests
{
    // ── Defaults ────────────────────────────────────────────────────

    [Fact]
    public void Defaults_Connection_AreCorrect()
    {
        var opts = new SftpEndpointOptions();

        opts.Host.Should().Be("localhost");
        opts.Port.Should().Be(22);
        opts.Username.Should().BeEmpty();
        opts.Password.Should().BeEmpty();
        opts.PrivateKeyPath.Should().BeEmpty();
        opts.PrivateKeyPassphrase.Should().BeEmpty();
        opts.UseKeyboardInteractive.Should().BeFalse();
        opts.PreferredAuthentications.Should().BeEmpty();
        opts.ServerFingerprint.Should().BeEmpty();
        opts.StrictHostKeyChecking.Should().BeFalse();
        opts.KnownHostsFile.Should().BeEmpty();
        opts.ConnectionTimeout.Should().Be(30_000);
        opts.OperationTimeout.Should().Be(60_000);
        opts.KeepAliveInterval.Should().Be(0);
        opts.BufferSize.Should().Be(32_768);
        opts.Compression.Should().BeFalse();
    }

    [Fact]
    public void Defaults_Proxy_AreCorrect()
    {
        var opts = new SftpEndpointOptions();

        opts.ProxyType.Should().Be(SftpProxyType.None);
        opts.ProxyHost.Should().BeEmpty();
        opts.ProxyPort.Should().Be(1080);
        opts.ProxyUsername.Should().BeEmpty();
        opts.ProxyPassword.Should().BeEmpty();
    }

    [Fact]
    public void Defaults_Reconnection_AreCorrect()
    {
        var opts = new SftpEndpointOptions();

        opts.MaximumReconnectAttempts.Should().Be(3);
        opts.ReconnectDelay.Should().Be(1000);
        opts.Disconnect.Should().BeFalse();
    }

    [Fact]
    public void Defaults_Consumer_AreCorrect()
    {
        var opts = new SftpEndpointOptions();

        opts.Delay.Should().Be(60_000);
        opts.InitialDelay.Should().Be(1000);
        opts.Include.Should().BeEmpty();
        opts.Exclude.Should().BeEmpty();
        opts.Recursive.Should().BeFalse();
        opts.MaxDepth.Should().Be(0);
        opts.MinDepth.Should().Be(0);
        opts.SortBy.Should().Be(GenericFileSortBy.None);
        opts.MaxMessagesPerPoll.Should().Be(0);
        opts.MinAge.Should().Be(0);
        opts.MaxAge.Should().Be(0);
        opts.Noop.Should().BeFalse();
        opts.Delete.Should().BeFalse();
        opts.MoveTo.Should().BeEmpty();
        opts.MoveExisting.Should().Be(GenericFileExistStrategy.Override);
        opts.PreMove.Should().BeEmpty();
        opts.MoveFailed.Should().BeEmpty();
        opts.Idempotent.Should().BeFalse();
        opts.IdempotentKey.Should().BeEmpty();
        opts.DoneFileName.Should().BeEmpty();
        opts.Binary.Should().BeTrue();
        opts.Charset.Should().Be("utf-8");
        opts.StepWise.Should().BeTrue();
        opts.Separator.Should().Be(SftpSeparator.Auto);
        opts.IgnoreFileNotFoundOrPermissionError.Should().BeFalse();
        opts.StartingDirectoryMustExist.Should().BeTrue();
        opts.DirectoryMustExist.Should().BeFalse();
        opts.SendEmptyMessageWhenIdle.Should().BeFalse();
    }

    [Fact]
    public void Defaults_Producer_AreCorrect()
    {
        var opts = new SftpEndpointOptions();

        opts.FileName.Should().BeNull();
        opts.FileExist.Should().Be(GenericFileExistStrategy.Override);
        opts.MoveExistingFileStrategy.Should().Be(SftpMoveExistingStrategy.Timestamp);
        opts.TempPrefix.Should().BeEmpty();
        opts.TempFileName.Should().BeEmpty();
        opts.Chmod.Should().BeEmpty();
        opts.ChmodDirectory.Should().BeEmpty();
        opts.AutoCreate.Should().BeTrue();
        opts.AllowNullBody.Should().BeFalse();
        opts.EagerDeleteTargetFile.Should().BeTrue();
        opts.KeepLastModified.Should().BeFalse();
        opts.Flatten.Should().BeFalse();
        opts.JailStartingDirectory.Should().BeTrue();
        opts.AppendChars.Should().BeEmpty();
    }

    // ── Validation passes ───────────────────────────────────────────

    [Fact]
    public void Validate_MinimalValidOptions_Succeeds()
    {
        var opts = new SftpEndpointOptions
        {
            Host = "myserver",
            Username = "admin",
            Password = "secret"
        };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_PrivateKeyAuth_Succeeds()
    {
        var opts = new SftpEndpointOptions
        {
            Host = "myserver",
            Username = "admin",
            PrivateKeyPath = "/home/user/.ssh/id_rsa"
        };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_BothAuthMethods_Succeeds()
    {
        var opts = new SftpEndpointOptions
        {
            Host = "myserver",
            Username = "admin",
            Password = "pass",
            PrivateKeyPath = "/key"
        };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    // ── Connection validation ───────────────────────────────────────

    [Fact]
    public void Validate_EmptyHost_Throws()
    {
        var opts = new SftpEndpointOptions { Host = "", Username = "u", Password = "p" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Host*");
    }

    [Fact]
    public void Validate_WhitespaceHost_Throws()
    {
        var opts = new SftpEndpointOptions { Host = "  ", Username = "u", Password = "p" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Host*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void Validate_InvalidPort_Throws(int port)
    {
        var opts = new SftpEndpointOptions { Host = "h", Port = port, Username = "u", Password = "p" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("Port");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    [InlineData(2222)]
    [InlineData(65535)]
    public void Validate_ValidPort_Succeeds(int port)
    {
        var opts = new SftpEndpointOptions { Host = "h", Port = port, Username = "u", Password = "p" };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_EmptyUsername_Throws()
    {
        var opts = new SftpEndpointOptions { Host = "h", Username = "", Password = "p" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Username*");
    }

    [Fact]
    public void Validate_NoAuthMethod_Throws()
    {
        var opts = new SftpEndpointOptions { Host = "h", Username = "u", Password = "", PrivateKeyPath = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*authentication*");
    }

    // ── Consumer validation ─────────────────────────────────────────

    [Fact]
    public void Validate_NegativeDelay_Throws()
    {
        var opts = ValidOptions();
        opts.Delay = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("Delay");
    }

    [Fact]
    public void Validate_NegativeInitialDelay_Throws()
    {
        var opts = ValidOptions();
        opts.InitialDelay = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("InitialDelay");
    }

    [Fact]
    public void Validate_NoopAndDelete_Throws()
    {
        var opts = ValidOptions();
        opts.Noop = true;
        opts.Delete = true;
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Noop*Delete*");
    }

    [Fact]
    public void Validate_NoopAndMoveTo_Throws()
    {
        var opts = ValidOptions();
        opts.Noop = true;
        opts.MoveTo = ".done";
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Noop*MoveTo*");
    }

    [Fact]
    public void Validate_DeleteAndMoveTo_Throws()
    {
        var opts = ValidOptions();
        opts.Delete = true;
        opts.MoveTo = ".done";
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Delete*MoveTo*");
    }

    [Fact]
    public void Validate_NegativeMaxMessagesPerPoll_Throws()
    {
        var opts = ValidOptions();
        opts.MaxMessagesPerPoll = -5;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("MaxMessagesPerPoll");
    }

    [Fact]
    public void Validate_NegativeMaxDepth_Throws()
    {
        var opts = ValidOptions();
        opts.MaxDepth = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("MaxDepth");
    }

    [Fact]
    public void Validate_NegativeMinDepth_Throws()
    {
        var opts = ValidOptions();
        opts.MinDepth = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("MinDepth");
    }

    [Fact]
    public void Validate_NegativeMinAge_Throws()
    {
        var opts = ValidOptions();
        opts.MinAge = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("MinAge");
    }

    [Fact]
    public void Validate_NegativeMaxAge_Throws()
    {
        var opts = ValidOptions();
        opts.MaxAge = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("MaxAge");
    }

    // ── Timeout validation ──────────────────────────────────────────

    [Fact]
    public void Validate_NegativeConnectionTimeout_Throws()
    {
        var opts = ValidOptions();
        opts.ConnectionTimeout = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("ConnectionTimeout");
    }

    [Fact]
    public void Validate_NegativeOperationTimeout_Throws()
    {
        var opts = ValidOptions();
        opts.OperationTimeout = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("OperationTimeout");
    }

    // ── Buffer validation ───────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    [InlineData(1023)]
    public void Validate_BufferSizeTooSmall_Throws(int size)
    {
        var opts = ValidOptions();
        opts.BufferSize = size;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("BufferSize");
    }

    [Fact]
    public void Validate_BufferSizeMinimum_Succeeds()
    {
        var opts = ValidOptions();
        opts.BufferSize = 1024;
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    // ── Reconnection validation ─────────────────────────────────────

    [Fact]
    public void Validate_NegativeMaxReconnectAttempts_Throws()
    {
        var opts = ValidOptions();
        opts.MaximumReconnectAttempts = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("MaximumReconnectAttempts");
    }

    [Fact]
    public void Validate_NegativeReconnectDelay_Throws()
    {
        var opts = ValidOptions();
        opts.ReconnectDelay = -1;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("ReconnectDelay");
    }

    // ── Proxy validation ────────────────────────────────────────────

    [Theory]
    [InlineData(SftpProxyType.Socks4)]
    [InlineData(SftpProxyType.Socks5)]
    [InlineData(SftpProxyType.Http)]
    public void Validate_ProxyTypeWithoutHost_Throws(SftpProxyType proxyType)
    {
        var opts = ValidOptions();
        opts.ProxyType = proxyType;
        opts.ProxyHost = "";
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ProxyHost*");
    }

    [Fact]
    public void Validate_ProxyNoneWithoutHost_Succeeds()
    {
        var opts = ValidOptions();
        opts.ProxyType = SftpProxyType.None;
        opts.ProxyHost = "";
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_InvalidProxyPort_Throws(int port)
    {
        var opts = ValidOptions();
        opts.ProxyPort = port;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("ProxyPort");
    }

    // ── Chmod validation ────────────────────────────────────────────

    [Theory]
    [InlineData("0644")]
    [InlineData("0755")]
    [InlineData("0777")]
    [InlineData("0600")]
    [InlineData("0000")]
    public void Validate_ValidChmod_Succeeds(string chmod)
    {
        var opts = ValidOptions();
        opts.Chmod = chmod;
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("9999")]
    [InlineData("0888")]
    [InlineData("rwxr-xr-x")]
    public void Validate_InvalidChmod_Throws(string chmod)
    {
        var opts = ValidOptions();
        opts.Chmod = chmod;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Chmod*");
    }

    [Theory]
    [InlineData("0755")]
    [InlineData("0700")]
    public void Validate_ValidChmodDirectory_Succeeds(string chmod)
    {
        var opts = ValidOptions();
        opts.ChmodDirectory = chmod;
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("0888")]
    public void Validate_InvalidChmodDirectory_Throws(string chmod)
    {
        var opts = ValidOptions();
        opts.ChmodDirectory = chmod;
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ChmodDirectory*");
    }

    // ── ParseOctalPermissions ───────────────────────────────────────

    [Theory]
    [InlineData("0644", 0x1A4)]   // 420 decimal
    [InlineData("0755", 0x1ED)]   // 493 decimal
    [InlineData("0777", 0x1FF)]   // 511 decimal
    [InlineData("0600", 0x180)]   // 384 decimal
    public void ParseOctalPermissions_ReturnsCorrectValue(string octal, short expected)
    {
        SftpEndpointOptions.ParseOctalPermissions(octal).Should().Be(expected);
    }

    // ── Post-processing combinations ────────────────────────────────

    [Fact]
    public void Validate_NoopOnly_Succeeds()
    {
        var opts = ValidOptions();
        opts.Noop = true;
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_DeleteOnly_Succeeds()
    {
        var opts = ValidOptions();
        opts.Delete = true;
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MoveToOnly_Succeeds()
    {
        var opts = ValidOptions();
        opts.MoveTo = ".done";
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    // ── BindFromUri ─────────────────────────────────────────────────

    [Fact]
    public void BindFromUri_SetsConnectionOptions()
    {
        var opts = new SftpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "myserver.com",
            ["port"] = "2222",
            ["username"] = "admin",
            ["password"] = "secret123",
            ["connectionTimeout"] = "5000",
            ["operationTimeout"] = "10000",
            ["keepAliveInterval"] = "3000",
            ["bufferSize"] = "65536",
            ["strictHostKeyChecking"] = "true",
            ["serverFingerprint"] = "AA:BB:CC"
        });

        opts.Host.Should().Be("myserver.com");
        opts.Port.Should().Be(2222);
        opts.Username.Should().Be("admin");
        opts.Password.Should().Be("secret123");
        opts.ConnectionTimeout.Should().Be(5000);
        opts.OperationTimeout.Should().Be(10000);
        opts.KeepAliveInterval.Should().Be(3000);
        opts.BufferSize.Should().Be(65536);
        opts.StrictHostKeyChecking.Should().BeTrue();
        opts.ServerFingerprint.Should().Be("AA:BB:CC");
    }

    [Fact]
    public void BindFromUri_SetsConsumerOptions()
    {
        var opts = new SftpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["delay"] = "5000",
            ["initialDelay"] = "2000",
            ["include"] = "*.csv",
            ["exclude"] = "*.tmp",
            ["recursive"] = "true",
            ["maxDepth"] = "3",
            ["sortBy"] = "Name",
            ["maxMessagesPerPoll"] = "10",
            ["minAge"] = "5000",
            ["maxAge"] = "60000",
            ["noop"] = "true",
            ["idempotent"] = "true",
            ["doneFileName"] = "${file:name}.done",
            ["stepWise"] = "false",
            ["sendEmptyMessageWhenIdle"] = "true"
        });

        opts.Delay.Should().Be(5000);
        opts.InitialDelay.Should().Be(2000);
        opts.Include.Should().Be("*.csv");
        opts.Exclude.Should().Be("*.tmp");
        opts.Recursive.Should().BeTrue();
        opts.MaxDepth.Should().Be(3);
        opts.SortBy.Should().Be(GenericFileSortBy.Name);
        opts.MaxMessagesPerPoll.Should().Be(10);
        opts.MinAge.Should().Be(5000);
        opts.MaxAge.Should().Be(60000);
        opts.Noop.Should().BeTrue();
        opts.Idempotent.Should().BeTrue();
        opts.DoneFileName.Should().Be("${file:name}.done");
        opts.StepWise.Should().BeFalse();
        opts.SendEmptyMessageWhenIdle.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_SetsProducerOptions()
    {
        var opts = new SftpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["fileExist"] = "Fail",
            ["tempPrefix"] = ".redb_",
            ["chmod"] = "0644",
            ["chmodDirectory"] = "0755",
            ["autoCreate"] = "true",
            ["allowNullBody"] = "true",
            ["eagerDeleteTargetFile"] = "false",
            ["keepLastModified"] = "true",
            ["flatten"] = "true",
            ["jailStartingDirectory"] = "false"
        });

        opts.FileExist.Should().Be(GenericFileExistStrategy.Fail);
        opts.TempPrefix.Should().Be(".redb_");
        opts.Chmod.Should().Be("0644");
        opts.ChmodDirectory.Should().Be("0755");
        opts.AutoCreate.Should().BeTrue();
        opts.AllowNullBody.Should().BeTrue();
        opts.EagerDeleteTargetFile.Should().BeFalse();
        opts.KeepLastModified.Should().BeTrue();
        opts.Flatten.Should().BeTrue();
        opts.JailStartingDirectory.Should().BeFalse();
    }

    [Fact]
    public void BindFromUri_SetsProxyOptions()
    {
        var opts = new SftpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["proxyType"] = "Socks5",
            ["proxyHost"] = "proxy.corp.com",
            ["proxyPort"] = "8080",
            ["proxyUsername"] = "proxy_user",
            ["proxyPassword"] = "proxy_pass"
        });

        opts.ProxyType.Should().Be(SftpProxyType.Socks5);
        opts.ProxyHost.Should().Be("proxy.corp.com");
        opts.ProxyPort.Should().Be(8080);
        opts.ProxyUsername.Should().Be("proxy_user");
        opts.ProxyPassword.Should().Be("proxy_pass");
    }

    [Fact]
    public void BindFromUri_SetsReconnectionOptions()
    {
        var opts = new SftpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["maximumReconnectAttempts"] = "5",
            ["reconnectDelay"] = "2000",
            ["disconnect"] = "true"
        });

        opts.MaximumReconnectAttempts.Should().Be(5);
        opts.ReconnectDelay.Should().Be(2000);
        opts.Disconnect.Should().BeTrue();
    }

    // ── Helper ──────────────────────────────────────────────────────

    private static SftpEndpointOptions ValidOptions() => new()
    {
        Host = "myserver",
        Username = "admin",
        Password = "secret"
    };
}
