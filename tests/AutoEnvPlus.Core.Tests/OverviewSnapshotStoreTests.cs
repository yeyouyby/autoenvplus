using AutoEnvPlus.Core.Overview;

namespace AutoEnvPlus.Core.Tests;

public sealed class OverviewSnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-OverviewSnapshot-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_ReturnsNullWhenSnapshotDoesNotExist()
    {
        OverviewSnapshot? snapshot = await new OverviewSnapshotStore(_root).LoadAsync();

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsSnapshot()
    {
        OverviewSnapshot expected = CreateSnapshot();
        OverviewSnapshotStore store = new(_root);

        await store.SaveAsync(expected);
        OverviewSnapshot? actual = await store.LoadAsync();

        Assert.NotNull(actual);
        Assert.Equal(expected.CapturedAtUtc, actual.CapturedAtUtc);
        Assert.Equal(OverviewSnapshotDepth.Full, actual.Depth);
        Assert.Equal("python", Assert.Single(actual.Languages).LanguageId);
        Assert.Equal(1, actual.Path?.CommandConflictCount);
        Assert.Equal(2, actual.Projects.Count);
        Assert.Equal(3, actual.Providers.ImportedProviderCount);
    }

    [Fact]
    public async Task LoadAsync_RejectsDuplicateProperties()
    {
        OverviewSnapshotStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SnapshotPath)!);
        await File.WriteAllTextAsync(
            store.SnapshotPath,
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"snapshot\":{}}");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync());

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_RejectsInvalidCounts()
    {
        OverviewSnapshot invalid = CreateSnapshot() with
        {
            Downloads = new OverviewDownloadStatus(-1, 0),
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new OverviewSnapshotStore(_root).SaveAsync(invalid));
    }

    private OverviewSnapshot CreateSnapshot()
    {
        DateTimeOffset timestamp = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        return new OverviewSnapshot(
            timestamp,
            OverviewSnapshotDepth.Full,
            timestamp,
            _root,
            [new OverviewLanguageStatus("python", "Python", "3.14.0", "CPython · global")],
            new OverviewPathStatus(12, 1, 2, 1, "1 command conflict"),
            new OverviewStorageStatus(1_024, 3, 2, "pip", 900, 10_000, []),
            new OverviewNetworkStatus(1, 2, "2 tool overrides", "HTTPS proxy configured"),
            new OverviewDownloadStatus(4, 2_048),
            new OverviewProviderStatus(4, 3, 2, 0),
            [
                new OverviewProjectStatus(Path.Combine(_root, "one"), timestamp),
                new OverviewProjectStatus(Path.Combine(_root, "two"), timestamp.AddMinutes(-1)),
            ],
            [new OverviewActivityStatus("Install", "Installed Python", timestamp)],
            []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
