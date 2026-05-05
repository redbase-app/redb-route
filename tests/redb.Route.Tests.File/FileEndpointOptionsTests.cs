using redb.Route.File;
using redb.Route.GenericFile;

namespace redb.Route.Tests.File;

/// <summary>
/// Tests for FileEndpointOptions validation.
/// </summary>
public class FileEndpointOptionsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var options = new FileEndpointOptions();

        options.Delay.Should().Be(500);
        options.InitialDelay.Should().Be(0);
        options.Include.Should().BeEmpty();
        options.Exclude.Should().BeEmpty();
        options.Recursive.Should().BeFalse();
        options.SortBy.Should().Be(GenericFileSortBy.None);
        options.MaxMessagesPerPoll.Should().Be(0);
        options.Noop.Should().BeFalse();
        options.Delete.Should().BeFalse();
        options.MoveTo.Should().BeEmpty();
        options.Idempotent.Should().BeFalse();
        options.ReadLock.Should().Be(ReadLockStrategy.None);
        options.ReadLockTimeout.Should().Be(10000);
        options.ReadLockCheckInterval.Should().Be(1000);
        options.ReadLockMinAge.Should().Be(1000);
        options.ReadLockMarkerFileExtension.Should().Be(".redbLock");
        options.DoneFileName.Should().BeEmpty();
        options.FileExist.Should().Be(GenericFileExistStrategy.Override);
        options.TempPrefix.Should().BeEmpty();
        options.Charset.Should().Be("utf-8");
        options.AutoCreate.Should().BeTrue();
        options.AllowNullBody.Should().BeFalse();
        options.AppendChars.Should().BeEmpty();
        options.EagerDeleteTargetFile.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidOptions_NoException()
    {
        var options = new FileEndpointOptions();
        options.Validate(); // Should not throw
    }

    [Fact]
    public void Validate_NegativeDelay_Throws()
    {
        var options = new FileEndpointOptions { Delay = -1 };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("Delay");
    }

    [Fact]
    public void Validate_NoopAndDelete_Throws()
    {
        var options = new FileEndpointOptions { Noop = true, Delete = true };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Noop*Delete*");
    }

    [Fact]
    public void Validate_NoopAndMoveTo_Throws()
    {
        var options = new FileEndpointOptions { Noop = true, MoveTo = "C:/archive" };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Noop*MoveTo*");
    }

    [Fact]
    public void Validate_DeleteAndMoveTo_Throws()
    {
        var options = new FileEndpointOptions { Delete = true, MoveTo = "C:/archive" };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Delete*MoveTo*");
    }

    [Fact]
    public void Validate_NegativeReadLockCheckInterval_Throws()
    {
        var options = new FileEndpointOptions { ReadLockCheckInterval = -1 };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_ZeroReadLockCheckInterval_Throws()
    {
        var options = new FileEndpointOptions { ReadLockCheckInterval = 0 };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_NegativeReadLockMinAge_Throws()
    {
        var options = new FileEndpointOptions { ReadLockMinAge = -1 };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_NegativeReadLockTimeout_Throws()
    {
        var options = new FileEndpointOptions { ReadLockTimeout = -1 };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_NegativeMaxMessagesPerPoll_Throws()
    {
        var options = new FileEndpointOptions { MaxMessagesPerPoll = -1 };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_NoopOnly_Valid()
    {
        var options = new FileEndpointOptions { Noop = true };
        options.Validate(); // No exception
    }

    [Fact]
    public void Validate_DeleteOnly_Valid()
    {
        var options = new FileEndpointOptions { Delete = true };
        options.Validate(); // No exception
    }

    [Fact]
    public void Validate_MoveToOnly_Valid()
    {
        var options = new FileEndpointOptions { MoveTo = "C:/archive" };
        options.Validate(); // No exception
    }
}
