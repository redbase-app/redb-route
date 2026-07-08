using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

[Collection("FirebaseEnvSensitive")]
public sealed class FirebaseStorageEndpointOptionsTests
{
    // ── BindFromUri ──

    [Fact]
    public void BindFromUri_Operation_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["operation"] = "Download"
        });
        options.Operation.Should().Be(FirebaseStorageOperationType.Download);
    }

    [Fact]
    public void BindFromUri_ContentType_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["contentType"] = "application/json"
        });
        options.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void BindFromUri_Delay_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["delay"] = "10000"
        });
        options.Delay.Should().Be(10000);
    }

    [Fact]
    public void BindFromUri_MaxMessagesPerPoll_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["maxMessagesPerPoll"] = "50"
        });
        options.MaxMessagesPerPoll.Should().Be(50);
    }

    [Fact]
    public void BindFromUri_DeleteAfterRead_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["deleteAfterRead"] = "true"
        });
        options.DeleteAfterRead.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_StreamBody_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["streamBody"] = "true"
        });
        options.StreamBody.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_Idempotent_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["idempotent"] = "true"
        });
        options.Idempotent.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_Include_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["include"] = "*.csv"
        });
        options.Include.Should().Be("*.csv");
    }

    [Fact]
    public void BindFromUri_Exclude_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["exclude"] = "*.tmp"
        });
        options.Exclude.Should().Be("*.tmp");
    }

    [Fact]
    public void BindFromUri_MoveAfterRead_Parsed()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["moveAfterRead"] = "archive/"
        });
        options.MoveAfterRead.Should().Be("archive/");
    }

    // ── Validate ──

    [Fact]
    public void Validate_NoCredential_NoEnvVar_Throws()
    {
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", null);
        Environment.SetEnvironmentVariable("FIREBASE_STORAGE_EMULATOR_HOST", null);
        var options = new FirebaseStorageEndpointOptions();
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*CredentialPath*");
    }

    [Fact]
    public void Validate_WithCredentialPath_DoesNotThrow()
    {
        var options = new FirebaseStorageEndpointOptions { CredentialPath = "/sa.json" };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithConnectionFactory_DoesNotThrow()
    {
        var options = new FirebaseStorageEndpointOptions { ConnectionFactory = "factory1" };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(99)]
    public void Validate_DelayTooLow_Throws(int delay)
    {
        var options = new FirebaseStorageEndpointOptions
        {
            CredentialPath = "/sa.json",
            Delay = delay
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Delay*");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(5000)]
    public void Validate_ValidDelay_DoesNotThrow(int delay)
    {
        var options = new FirebaseStorageEndpointOptions
        {
            CredentialPath = "/sa.json",
            Delay = delay
        };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_InvalidMaxMessagesPerPoll_Throws(int count)
    {
        var options = new FirebaseStorageEndpointOptions
        {
            CredentialPath = "/sa.json",
            MaxMessagesPerPoll = count
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*MaxMessagesPerPoll*");
    }

    // ── Defaults ──

    [Fact]
    public void DefaultOperation_IsUpload()
    {
        new FirebaseStorageEndpointOptions().Operation.Should().Be(FirebaseStorageOperationType.Upload);
    }

    [Fact]
    public void DefaultDelay_Is5000()
    {
        new FirebaseStorageEndpointOptions().Delay.Should().Be(5000);
    }

    [Fact]
    public void DefaultMaxMessagesPerPoll_Is10()
    {
        new FirebaseStorageEndpointOptions().MaxMessagesPerPoll.Should().Be(10);
    }

    [Fact]
    public void DefaultDeleteAfterRead_IsFalse()
    {
        new FirebaseStorageEndpointOptions().DeleteAfterRead.Should().BeFalse();
    }

    [Fact]
    public void DefaultIdempotent_IsFalse()
    {
        new FirebaseStorageEndpointOptions().Idempotent.Should().BeFalse();
    }

    [Fact]
    public void DefaultStreamBody_IsFalse()
    {
        new FirebaseStorageEndpointOptions().StreamBody.Should().BeFalse();
    }

    [Fact]
    public void DefaultIncludeBody_IsTrue()
    {
        new FirebaseStorageEndpointOptions().IncludeBody.Should().BeTrue();
    }
}
