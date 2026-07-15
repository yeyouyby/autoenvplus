using System.Text;
using AutoEnvPlus.Core.Activity;

namespace AutoEnvPlus.Core.Tests;

public sealed class ActivityLogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-Activity-{Guid.NewGuid():N}");

    [Fact]
    public async Task AppendAsync_SanitizesSecretsAndRoundTripsRecord()
    {
        ActivityLogStore store = new(_root);
        string affectedPath = Path.Combine(_root, "runtimes", "python");

        ActivityLogEntry written = await store.AppendAsync(
            ActivityOperationType.RuntimeInstall,
            ActivityStatus.Failed,
            "download failed password=super-secret token=abc123 --pfx-password ultra-secret Bearer eyJhbGciOiJ9 https://user:pass@example.test/pkg \"apiKey\":\"json-secret\" Authorization: Basic YmFzZTY0LXNlY3JldA==",
            [affectedPath],
            snapshotPath: Path.Combine(_root, "state", "snapshot.json"),
            rollbackPath: Path.Combine(_root, "state", "rollback.json"));

        ActivityLogLoadResult loaded = await new ActivityLogStore(_root).LoadAsync();
        ActivityLogEntry entry = Assert.Single(loaded.Entries);

        Assert.Equal(written.Id, entry.Id);
        Assert.Equal(ActivityStatus.Failed, entry.Status);
        Assert.Contains("<redacted>", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("ultra-secret", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJ9", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass@", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("json-secret", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("YmFzZTY0LXNlY3JldA", entry.Summary, StringComparison.Ordinal);

        string raw = await File.ReadAllTextAsync(store.LogPath);
        Assert.DoesNotContain("super-secret", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ultra-secret", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("json-secret", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("YmFzZTY0LXNlY3JldA", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_MissingManagedRootReturnsEmpty()
    {
        ActivityLogStore store = new(_root);

        ActivityLogLoadResult loaded = await store.LoadAsync();

        Assert.Empty(loaded.Entries);
        Assert.Empty(loaded.Errors);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ConcurrentAppends_AreSerializedAndBounded()
    {
        ActivityLogStore store = new(_root, maxEntries: 25, maxBytes: 32 * 1024);

        Task[] writes = Enumerable.Range(0, 100)
            .Select(index => store.AppendAsync(
                ActivityOperationType.Other,
                ActivityStatus.Succeeded,
                $"operation {index}"))
            .ToArray();
        await Task.WhenAll(writes);

        ActivityLogLoadResult loaded = await store.LoadAsync();
        Assert.Equal(25, loaded.Entries.Count);
        Assert.Equal(25, loaded.Entries.Select(entry => entry.Id).Distinct().Count());
        Assert.True(new FileInfo(store.LogPath).Length <= 32 * 1024);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(store.LogPath)!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task TwoStoreInstances_DoNotLoseConcurrentRecords()
    {
        ActivityLogStore first = new(_root, maxEntries: 100, maxBytes: 64 * 1024);
        ActivityLogStore second = new(_root, maxEntries: 100, maxBytes: 64 * 1024);

        Task[] writes = Enumerable.Range(0, 40)
            .Select(index => (index % 2 == 0 ? first : second).AppendAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Succeeded,
                $"path operation {index}"))
            .ToArray();
        await Task.WhenAll(writes);

        ActivityLogLoadResult loaded = await first.LoadAsync();
        Assert.Equal(40, loaded.Entries.Count);
        Assert.Equal(40, loaded.Entries.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public async Task LoadAsync_SkipsMalformedAndUnsafeLines()
    {
        ActivityLogStore store = new(_root);
        await store.AppendAsync(
            ActivityOperationType.DiagnosticExport,
            ActivityStatus.Succeeded,
            "diagnostics exported");

        await File.AppendAllTextAsync(
            store.LogPath,
            "{ malformed json\n"
            + "{\"schemaVersion\":999,\"id\":\""
            + Guid.NewGuid().ToString("D")
            + "\",\"timestampUtc\":\"2026-07-14T00:00:00Z\",\"operationType\":\"Other\",\"status\":\"Succeeded\",\"summary\":\"future\"}\n",
            Encoding.UTF8);

        ActivityLogLoadResult loaded = await store.LoadAsync();

        Assert.Single(loaded.Entries);
        Assert.Equal(2, loaded.Errors.Count);
        Assert.All(loaded.Errors, error => Assert.Contains("line", error, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_RejectsOversizedFileBeforeReading()
    {
        ActivityLogStore store = new(_root, maxBytes: 1_024);
        Directory.CreateDirectory(Path.GetDirectoryName(store.LogPath)!);
        await File.WriteAllTextAsync(store.LogPath, new string('x', 2_048));

        ActivityLogLoadResult loaded = await store.LoadAsync();

        Assert.Empty(loaded.Entries);
        Assert.Single(loaded.Errors);
        Assert.Contains("size limit", loaded.Errors[0], StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAnyAsync<IOException>(() => store.AppendAsync(
            ActivityOperationType.Other,
            ActivityStatus.Succeeded,
            "must not rewrite oversized log"));
    }

    [Fact]
    public async Task Constructor_RejectsLogOutsideManagedRoot()
    {
        string outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside-activity.jsonl");

        Assert.Throws<ArgumentException>(() => new ActivityLogStore(_root, outside));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AppendAsync_RejectsUriAffectedPath()
    {
        ActivityLogStore store = new(_root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(
            ActivityOperationType.Other,
            ActivityStatus.Succeeded,
            "safe summary",
            ["https://example.test/?token=secret"]));
        Assert.False(File.Exists(store.LogPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
