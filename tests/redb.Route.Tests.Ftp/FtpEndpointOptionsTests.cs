using redb.Route.GenericFile;
using redb.Route.Ftp;

namespace redb.Route.Tests.Ftp;

public sealed class FtpEndpointOptionsTests
{
    // ── Defaults ────────────────────────────────────────────────────

    [Fact]
    public void Defaults_Connection_AreCorrect()
    {
        var opts = new FtpEndpointOptions();

        opts.Host.Should().Be("localhost");
        opts.Port.Should().Be(21);
        opts.Username.Should().BeEmpty();
        opts.Password.Should().BeEmpty();
        opts.ConnectionTimeout.Should().Be(30_000);
        opts.OperationTimeout.Should().Be(60_000);
        opts.PassiveMode.Should().BeTrue();
    }

    [Fact]
    public void Defaults_Tls_AreCorrect()
    {
        var opts = new FtpEndpointOptions();

        opts.UseFtps.Should().BeFalse();
        opts.ValidateCertificate.Should().BeTrue();
    }

    [Fact]
    public void Defaults_Transfer_AreCorrect()
    {
        var opts = new FtpEndpointOptions();

        opts.TransferType.Should().Be(FtpTransferType.Binary);
        opts.IgnoreFileNotFoundOrPermissionError.Should().BeFalse();
    }

    [Fact]
    public void Defaults_Reconnection_AreCorrect()
    {
        var opts = new FtpEndpointOptions();

        opts.MaximumReconnectAttempts.Should().Be(3);
        opts.ReconnectDelay.Should().Be(1000);
        opts.Disconnect.Should().BeFalse();
    }

    [Fact]
    public void Defaults_Consumer_AreCorrect()
    {
        var opts = new FtpEndpointOptions();

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
        opts.Charset.Should().Be("utf-8");
        opts.StartingDirectoryMustExist.Should().BeTrue();
        opts.SendEmptyMessageWhenIdle.Should().BeFalse();
    }

    [Fact]
    public void Defaults_Producer_AreCorrect()
    {
        var opts = new FtpEndpointOptions();

        opts.FileName.Should().BeNull();
        opts.FileExist.Should().Be(GenericFileExistStrategy.Override);
        opts.MoveExistingFileStrategy.Should().Be(FtpMoveExistingStrategy.Timestamp);
        opts.TempPrefix.Should().BeEmpty();
        opts.TempFileName.Should().BeEmpty();
        opts.AutoCreate.Should().BeTrue();
        opts.AllowNullBody.Should().BeFalse();
        opts.EagerDeleteTargetFile.Should().BeTrue();
        opts.Flatten.Should().BeFalse();
        opts.JailStartingDirectory.Should().BeTrue();
        opts.AppendChars.Should().BeEmpty();
    }

    // ── Validation passes ───────────────────────────────────────────

    [Fact]
    public void Validate_MinimalValidOptions_Succeeds()
    {
        var opts = new FtpEndpointOptions
        {
            Host = "myserver"
        };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithCredentials_Succeeds()
    {
        var opts = new FtpEndpointOptions
        {
            Host = "myserver",
            Username = "admin",
            Password = "secret"
        };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    // ── Connection validation ───────────────────────────────────────

    [Fact]
    public void Validate_EmptyHost_Throws()
    {
        var opts = new FtpEndpointOptions { Host = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Host*");
    }

    [Fact]
    public void Validate_WhitespaceHost_Throws()
    {
        var opts = new FtpEndpointOptions { Host = "  " };
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
        var opts = new FtpEndpointOptions { Host = "h", Port = port };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("Port");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(21)]
    [InlineData(2121)]
    [InlineData(65535)]
    public void Validate_ValidPort_Succeeds(int port)
    {
        var opts = new FtpEndpointOptions { Host = "h", Port = port };
        var act = () => opts.Validate();
        act.Should().NotThrow();
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
        var opts = new FtpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["host"] = "myserver.com",
            ["port"] = "2121",
            ["username"] = "admin",
            ["password"] = "secret123",
            ["connectionTimeout"] = "5000",
            ["operationTimeout"] = "10000",
            ["passiveMode"] = "false"
        });

        opts.Host.Should().Be("myserver.com");
        opts.Port.Should().Be(2121);
        opts.Username.Should().Be("admin");
        opts.Password.Should().Be("secret123");
        opts.ConnectionTimeout.Should().Be(5000);
        opts.OperationTimeout.Should().Be(10000);
        opts.PassiveMode.Should().BeFalse();
    }

    [Fact]
    public void BindFromUri_SetsTlsOptions()
    {
        var opts = new FtpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["useFtps"] = "true",
            ["validateCertificate"] = "false"
        });

        opts.UseFtps.Should().BeTrue();
        opts.ValidateCertificate.Should().BeFalse();
    }

    [Fact]
    public void BindFromUri_SetsConsumerOptions()
    {
        var opts = new FtpEndpointOptions();
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
        opts.SendEmptyMessageWhenIdle.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_SetsProducerOptions()
    {
        var opts = new FtpEndpointOptions();
        opts.BindFromUri(new Dictionary<string, string>
        {
            ["fileExist"] = "Fail",
            ["tempPrefix"] = ".redb_",
            ["autoCreate"] = "true",
            ["allowNullBody"] = "true",
            ["eagerDeleteTargetFile"] = "false",
            ["flatten"] = "true",
            ["jailStartingDirectory"] = "false"
        });

        opts.FileExist.Should().Be(GenericFileExistStrategy.Fail);
        opts.TempPrefix.Should().Be(".redb_");
        opts.AutoCreate.Should().BeTrue();
        opts.AllowNullBody.Should().BeTrue();
        opts.EagerDeleteTargetFile.Should().BeFalse();
        opts.Flatten.Should().BeTrue();
        opts.JailStartingDirectory.Should().BeFalse();
    }

    [Fact]
    public void BindFromUri_SetsReconnectionOptions()
    {
        var opts = new FtpEndpointOptions();
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

    private static FtpEndpointOptions ValidOptions() => new()
    {
        Host = "myserver"
    };
}
