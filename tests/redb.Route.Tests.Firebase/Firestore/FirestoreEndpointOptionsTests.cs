using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

[Collection("FirebaseEnvSensitive")]
public sealed class FirestoreEndpointOptionsTests
{
    // ── BindFromUri ──

    [Fact]
    public void BindFromUri_Operation_Parsed()
    {
        var options = new FirestoreEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["operation"] = "Query"
        });
        options.Operation.Should().Be(FirestoreOperationType.Query);
    }

    [Fact]
    public void BindFromUri_Where_Parsed()
    {
        var options = new FirestoreEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["where"] = "status==pending"
        });
        options.Where.Should().Be("status==pending");
    }

    [Fact]
    public void BindFromUri_OrderBy_Parsed()
    {
        var options = new FirestoreEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["orderBy"] = "createdAt desc"
        });
        options.OrderBy.Should().Be("createdAt desc");
    }

    [Fact]
    public void BindFromUri_Limit_Parsed()
    {
        var options = new FirestoreEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["limit"] = "50"
        });
        options.Limit.Should().Be(50);
    }

    [Fact]
    public void BindFromUri_Merge_Parsed()
    {
        var options = new FirestoreEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["merge"] = "true"
        });
        options.Merge.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_Realtime_Parsed()
    {
        var options = new FirestoreEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["realtime"] = "false"
        });
        options.Realtime.Should().BeFalse();
    }

    [Fact]
    public void BindFromUri_Delay_Parsed()
    {
        var options = new FirestoreEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["delay"] = "10000"
        });
        options.Delay.Should().Be(10000);
    }

    [Fact]
    public void BindFromUri_RawJson_Parsed()
    {
        var options = new FirestoreEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["rawJson"] = "true"
        });
        options.RawJson.Should().BeTrue();
    }

    // ── Validate ──

    [Fact]
    public void Validate_NoCredential_NoEnvVar_Throws()
    {
        var prevCreds = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        var prevEmu = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", null);
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", null);
        try
        {
            var options = new FirestoreEndpointOptions();
            var act = () => options.Validate();
            act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*CredentialPath*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", prevCreds);
            Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", prevEmu);
        }
    }

    [Fact]
    public void Validate_WithCredentialPath_DoesNotThrow()
    {
        var options = new FirestoreEndpointOptions { CredentialPath = "/sa.json" };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithConnectionFactory_DoesNotThrow()
    {
        var options = new FirestoreEndpointOptions { ConnectionFactory = "factory1" };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithEmulatorHost_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", "localhost:8080");
        try
        {
            var options = new FirestoreEndpointOptions();
            var act = () => options.Validate();
            act.Should().NotThrow();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", null);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(99)]
    public void Validate_DelayTooLow_Throws(int delay)
    {
        var options = new FirestoreEndpointOptions
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
        var options = new FirestoreEndpointOptions
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
    public void Validate_InvalidLimit_Throws(int limit)
    {
        var options = new FirestoreEndpointOptions
        {
            CredentialPath = "/sa.json",
            Limit = limit
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Limit*");
    }

    [Fact]
    public void Validate_NullLimit_DoesNotThrow()
    {
        var options = new FirestoreEndpointOptions
        {
            CredentialPath = "/sa.json",
            Limit = null
        };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    // ── Defaults ──

    [Fact]
    public void DefaultOperation_IsSet()
    {
        new FirestoreEndpointOptions().Operation.Should().Be(FirestoreOperationType.Set);
    }

    [Fact]
    public void DefaultRealtime_IsTrue()
    {
        new FirestoreEndpointOptions().Realtime.Should().BeTrue();
    }

    [Fact]
    public void DefaultDelay_Is5000()
    {
        new FirestoreEndpointOptions().Delay.Should().Be(5000);
    }

    [Fact]
    public void DefaultRawJson_IsFalse()
    {
        new FirestoreEndpointOptions().RawJson.Should().BeFalse();
    }

    [Fact]
    public void DefaultMerge_IsFalse()
    {
        new FirestoreEndpointOptions().Merge.Should().BeFalse();
    }
}
