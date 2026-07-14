using AutoEnvPlus.Core.Installation;

namespace AutoEnvPlus.Core.Tests;

public sealed class StagingDirectoryReclaimerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Staging-{Guid.NewGuid():N}");

    [Fact]
    public void Reclaim_DeletesOnlyDirectoriesOlderThanThreshold()
    {
        string staging = Directory.CreateDirectory(Path.Combine(_root, ".staging")).FullName;
        string oldDirectory = Directory.CreateDirectory(Path.Combine(staging, "old-install")).FullName;
        string recentDirectory = Directory.CreateDirectory(Path.Combine(staging, "active-install")).FullName;
        File.WriteAllText(Path.Combine(oldDirectory, "package.zip"), "partial");
        File.WriteAllText(Path.Combine(recentDirectory, "package.zip"), "active");
        DateTimeOffset now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        Directory.SetLastWriteTimeUtc(oldDirectory, now.AddDays(-2).UtcDateTime);
        Directory.SetLastWriteTimeUtc(recentDirectory, now.AddMinutes(-10).UtcDateTime);

        StagingCleanupResult result = new StagingDirectoryReclaimer().Reclaim(
            _root,
            TimeSpan.FromHours(24),
            now);

        Assert.Equal(1, result.DeletedDirectories);
        Assert.Equal(1, result.RetainedDirectories);
        Assert.Empty(result.Errors);
        Assert.False(Directory.Exists(oldDirectory));
        Assert.True(Directory.Exists(recentDirectory));
    }

    [Fact]
    public void Reclaim_MissingStagingRootIsANoOp()
    {
        StagingCleanupResult result = new StagingDirectoryReclaimer().Reclaim(
            _root,
            TimeSpan.FromHours(24));

        Assert.Equal(0, result.DeletedDirectories);
        Assert.Equal(0, result.RetainedDirectories);
        Assert.Empty(result.Errors);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
