using AutoEnvPlus.Core.Languages;

namespace AutoEnvPlus.Core.Tests;

public sealed class LanguageToolInventoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-LanguageInventory-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_ReturnsNullWhenSnapshotDoesNotExist()
    {
        Assert.Null(await new LanguageToolInventoryStore(_root).LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_RoundTripsAndProjectsKnownCommands()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        LanguageToolInventorySnapshot expected = new(
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            LanguageToolInventorySnapshot.ComputeCatalogFingerprint(catalog),
            [new LanguageToolInventoryEntry(
                "cpython",
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
                ["python", "py"])]);
        LanguageToolInventoryStore store = new(_root);

        await store.SaveAsync(expected);
        LanguageToolInventorySnapshot? actual = await store.LoadAsync();

        Assert.NotNull(actual);
        Assert.True(actual.IsCompatibleWith(catalog));
        Assert.Contains("python", actual.GetDetectedCommands(catalog));
        Assert.DoesNotContain("unknown", actual.GetDetectedCommands(catalog));
    }

    [Fact]
    public async Task LoadAsync_RejectsDuplicateProperties()
    {
        LanguageToolInventoryStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SnapshotPath)!);
        await File.WriteAllTextAsync(
            store.SnapshotPath,
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"snapshot\":{}}");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync());

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_RejectsDuplicateToolIds()
    {
        LanguageToolInventorySnapshot invalid = new(
            DateTimeOffset.UtcNow,
            new string('a', 64),
            [
                new LanguageToolInventoryEntry("cpython", DateTimeOffset.UtcNow, ["python"]),
                new LanguageToolInventoryEntry("CPython", DateTimeOffset.UtcNow, ["py"]),
            ]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new LanguageToolInventoryStore(_root).SaveAsync(invalid));
    }

    [Fact]
    public void Merge_ReplacesOnlyScannedToolsAndKeepsKnownInventory()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        DateTimeOffset first = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset second = DateTimeOffset.UtcNow;
        LanguageToolInventorySnapshot current = new(
            first,
            LanguageToolInventorySnapshot.ComputeCatalogFingerprint(catalog),
            [
                new LanguageToolInventoryEntry("cpython", first, ["python"]),
                new LanguageToolInventoryEntry("nodejs", first, ["node"]),
            ]);
        LanguageToolInventorySnapshot update = new(
            second,
            LanguageToolInventorySnapshot.ComputeCatalogFingerprint(catalog),
            [new LanguageToolInventoryEntry("cpython", second, ["py"])]);

        LanguageToolInventorySnapshot merged = LanguageToolInventorySnapshot.Merge(
            catalog,
            current,
            update);

        Assert.Equal(2, merged.Tools.Count);
        Assert.Contains(merged.Tools, entry => entry.ToolId == "cpython"
            && entry.DetectedCommands.SequenceEqual(["py"]));
        Assert.Contains(merged.Tools, entry => entry.ToolId == "nodejs"
            && entry.DetectedCommands.SequenceEqual(["node"]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
