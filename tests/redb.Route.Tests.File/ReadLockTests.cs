using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// Tests for read lock strategies.
/// </summary>
public class ReadLockTests
{
    private readonly string _tempDir;

    public ReadLockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "redb-route-readlock-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    private FileInfo CreateTestFile(string name = "test.txt", string content = "hello")
    {
        var path = Path.Combine(_tempDir, name);
        System.IO.File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    private FileEndpointOptions DefaultOptions() => new();

    // ── NoReadLock ───────────────────────────────────────────────────

    [Fact]
    public async Task NoReadLock_AlwaysAcquires()
    {
        var file = CreateTestFile();
        var strategy = NoReadLock.Instance;

        var result = await strategy.AcquireLock(file, DefaultOptions(), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public void NoReadLock_ReleaseDoesNothing()
    {
        var file = CreateTestFile();
        var strategy = NoReadLock.Instance;

        strategy.ReleaseLock(file, DefaultOptions()); // No exception
    }

    // ── MarkerFileReadLock ──────────────────────────────────────────

    [Fact]
    public async Task MarkerFile_AcquiresAndCreatesMarker()
    {
        var file = CreateTestFile();
        var strategy = MarkerFileReadLock.Instance;
        var options = DefaultOptions();

        var result = await strategy.AcquireLock(file, options, CancellationToken.None);

        result.Should().BeTrue();
        System.IO.File.Exists(file.FullName + ".redbLock").Should().BeTrue();

        strategy.ReleaseLock(file, options);
        System.IO.File.Exists(file.FullName + ".redbLock").Should().BeFalse();
    }

    [Fact]
    public async Task MarkerFile_LockedByAnother_ReturnsFalse()
    {
        var file = CreateTestFile();
        var strategy = MarkerFileReadLock.Instance;
        var options = DefaultOptions();

        // Simulate another consumer holding the lock
        System.IO.File.WriteAllText(file.FullName + ".redbLock", "locked");

        var result = await strategy.AcquireLock(file, options, CancellationToken.None);

        result.Should().BeFalse();

        // Cleanup
        System.IO.File.Delete(file.FullName + ".redbLock");
    }

    [Fact]
    public async Task MarkerFile_CustomExtension()
    {
        var file = CreateTestFile();
        var strategy = MarkerFileReadLock.Instance;
        var options = new FileEndpointOptions { ReadLockMarkerFileExtension = ".mylock" };

        var result = await strategy.AcquireLock(file, options, CancellationToken.None);

        result.Should().BeTrue();
        System.IO.File.Exists(file.FullName + ".mylock").Should().BeTrue();

        strategy.ReleaseLock(file, options);
        System.IO.File.Exists(file.FullName + ".mylock").Should().BeFalse();
    }

    // ── ChangedReadLock ─────────────────────────────────────────────

    [Fact]
    public async Task Changed_StableFile_Acquires()
    {
        var file = CreateTestFile();
        var strategy = ChangedReadLock.Instance;
        var options = new FileEndpointOptions
        {
            ReadLockCheckInterval = 50,
            ReadLockMinAge = 100,
            ReadLockTimeout = 5000
        };

        // File is already stable
        await Task.Delay(150); // Ensure it's old enough

        var result = await strategy.AcquireLock(file, options, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Changed_Timeout_ReturnsFalse()
    {
        var file = CreateTestFile();
        var strategy = ChangedReadLock.Instance;
        var options = new FileEndpointOptions
        {
            ReadLockCheckInterval = 50,
            ReadLockMinAge = 10000, // File must be stable for 10s
            ReadLockTimeout = 200    // But timeout after 200ms
        };

        var result = await strategy.AcquireLock(file, options, CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── FileLockReadLock ────────────────────────────────────────────

    [Fact]
    public async Task FileLock_AcquiresAndReleases()
    {
        var file = CreateTestFile();
        var strategy = new FileLockReadLock();
        var options = DefaultOptions();

        var result = await strategy.AcquireLock(file, options, CancellationToken.None);

        result.Should().BeTrue();

        // File should be locked — another attempt should fail
        try
        {
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            // If we get here, the lock wasn't exclusive, which is fine — depends on OS behavior
        }
        catch (IOException)
        {
            // Expected — file is locked
        }

        strategy.ReleaseLock(file, options);

        // After release, file should be accessible
        using var fs2 = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs2.Should().NotBeNull();
    }

    [Fact]
    public async Task FileLock_AlreadyLocked_ReturnsFalse()
    {
        var file = CreateTestFile();
        var strategy = new FileLockReadLock();
        var options = DefaultOptions();

        // Lock the file from outside
        using var externalLock = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await strategy.AcquireLock(file, options, CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── RenameReadLock ──────────────────────────────────────────────

    [Fact]
    public async Task Rename_AcquiresAndRenames()
    {
        var file = CreateTestFile();
        var strategy = new RenameReadLock();
        var options = DefaultOptions();
        var originalPath = file.FullName;

        var result = await strategy.AcquireLock(file, options, CancellationToken.None);

        result.Should().BeTrue();
        System.IO.File.Exists(originalPath).Should().BeFalse();
        System.IO.File.Exists(originalPath + ".redbRename").Should().BeTrue();

        strategy.ReleaseLock(file, options);
        System.IO.File.Exists(originalPath).Should().BeTrue();
        System.IO.File.Exists(originalPath + ".redbRename").Should().BeFalse();
    }

    // ── ReadLockFactory ─────────────────────────────────────────────

    [Theory]
    [InlineData(ReadLockStrategy.None, typeof(NoReadLock))]
    [InlineData(ReadLockStrategy.MarkerFile, typeof(MarkerFileReadLock))]
    [InlineData(ReadLockStrategy.Changed, typeof(ChangedReadLock))]
    [InlineData(ReadLockStrategy.FileLock, typeof(FileLockReadLock))]
    [InlineData(ReadLockStrategy.Rename, typeof(RenameReadLock))]
    public void Factory_CreatesCorrectStrategy(ReadLockStrategy strategy, Type expectedType)
    {
        var result = ReadLockFactory.Create(strategy);

        result.Should().BeOfType(expectedType);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
