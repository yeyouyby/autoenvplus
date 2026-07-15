using System.Text.Json;
using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Storage;

namespace AutoEnvPlus.Core.Tests;

public sealed class CacheCleanupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-CacheCleanup-{Guid.NewGuid():N}");

    [Fact]
    public async Task CleanupAsync_MovesContentToSameVolumeAndRestoreReturnsIt()
    {
        string source = CreateCache();
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new(Path.Combine(_root, "managed"));

        CacheCleanupPlan plan = await service.CreatePlanAsync(location);

        Assert.Equal(2, plan.FileCount);
        Assert.Equal(11, plan.TotalBytes);
        Assert.Equal(
            Path.GetPathRoot(source),
            Path.GetPathRoot(plan.TrashPath),
            ignoreCase: true);
        CacheCleanupOperationResult cleanup = await service.CleanupAsync(plan);

        Assert.True(cleanup.Success);
        Assert.True(cleanup.RecoveryAvailable);
        Assert.True(Directory.Exists(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(source));
        Assert.True(File.Exists(cleanup.ManifestPath));

        CacheCleanupCatalog catalog = await service.DiscoverItemsAsync([location]);
        CacheCleanupItem item = Assert.Single(catalog.Items);
        Assert.Empty(catalog.Errors);
        Assert.True(item.CanRestore);
        Assert.Equal(2, item.FileCount);
        Assert.Equal(11, item.TotalBytes);

        CacheCleanupOperationResult restore = await service.RestoreAsync(item);

        Assert.True(restore.Success);
        Assert.False(restore.RecoveryAvailable);
        Assert.Equal("first", File.ReadAllText(Path.Combine(source, "one.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(source, "nested", "two.txt")));
        Assert.False(Directory.Exists(plan.TrashPath));
        Assert.Empty((await service.DiscoverItemsAsync([location])).Items);
    }

    [Fact]
    public async Task PurgeAsync_PermanentlyDeletesOnlyDiscoveredIsolationItem()
    {
        string source = CreateCache();
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(location);
        Assert.True((await service.CleanupAsync(plan)).Success);
        CacheCleanupItem item = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);

        CacheCleanupOperationResult purge = await service.PurgeAsync(item);

        Assert.True(purge.Success);
        Assert.False(purge.RecoveryAvailable);
        Assert.Equal(2, purge.FileCount);
        Assert.Equal(11, purge.TotalBytes);
        Assert.True(Directory.Exists(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(source));
        Assert.False(Directory.Exists(item.TrashPath));
        Assert.Empty((await service.DiscoverItemsAsync([location])).Items);
    }

    [Fact]
    public async Task RestoreAsync_RefusesNewerSourceDataWithoutOverwritingIt()
    {
        string source = CreateCache();
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(location);
        Assert.True((await service.CleanupAsync(plan)).Success);
        CacheCleanupItem item = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);
        File.WriteAllText(Path.Combine(source, "newer.txt"), "new data");

        CacheCleanupOperationResult restore = await service.RestoreAsync(item);
        CacheCleanupItem blocked = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);

        Assert.False(restore.Success);
        Assert.Contains("newer", restore.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(blocked.CanRestore);
        Assert.True(blocked.CanPurge);
        Assert.Contains("newer", blocked.RestoreBlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("new data", File.ReadAllText(Path.Combine(source, "newer.txt")));
        Assert.True(Directory.Exists(Path.Combine(item.TrashPath, "content")));

        CacheCleanupOperationResult purge = await service.PurgeAsync(blocked);

        Assert.True(purge.Success);
        Assert.Equal("new data", File.ReadAllText(Path.Combine(source, "newer.txt")));
    }

    [Fact]
    public async Task CleanupAsync_RejectsPlanAfterCacheChanged()
    {
        string source = CreateCache();
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(CreateLocation(source));
        File.WriteAllText(Path.Combine(source, "later.txt"), "later");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CleanupAsync(plan));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(plan.TrashPath));
        Assert.True(File.Exists(Path.Combine(source, "later.txt")));
    }

    [Fact]
    public async Task CleanupAsync_CancellationRollsMovedEntriesBack()
    {
        string source = CreateCache();
        File.WriteAllText(Path.Combine(source, "third.txt"), "third");
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(location);
        using CancellationTokenSource cancellation = new();
        ImmediateProgress<CacheCleanupProgress> progress = new(value =>
        {
            if (value.Stage == "move" && value.CompletedEntries == 1)
            {
                cancellation.Cancel();
            }
        });

        CacheCleanupOperationResult result = await service.CleanupAsync(
            plan,
            progress,
            cancellation.Token);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.False(result.RecoveryAvailable);
        Assert.Equal("first", File.ReadAllText(Path.Combine(source, "one.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(source, "nested", "two.txt")));
        Assert.Equal("third", File.ReadAllText(Path.Combine(source, "third.txt")));
        Assert.Empty((await service.DiscoverItemsAsync([location])).Items);
    }

    [Fact]
    public async Task PurgeAsync_CancellationMakesRestoreUnavailableAndCanBeRetried()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "purge-source")).FullName;
        File.WriteAllText(Path.Combine(source, "one.bin"), "1111");
        File.WriteAllText(Path.Combine(source, "two.bin"), "2222");
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(location);
        Assert.True((await service.CleanupAsync(plan)).Success);
        CacheCleanupItem item = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);
        using CancellationTokenSource cancellation = new();
        ImmediateProgress<CacheCleanupProgress> progress = new(value =>
        {
            if (value.Stage == "purge" && value.CompletedEntries == 1)
            {
                cancellation.Cancel();
            }
        });

        CacheCleanupOperationResult interrupted = await service.PurgeAsync(
            item,
            progress,
            cancellation.Token);

        Assert.False(interrupted.Success);
        Assert.True(interrupted.Cancelled);
        Assert.True(interrupted.PurgePending);
        Assert.False(interrupted.RecoveryAvailable);
        CacheCleanupItem pending = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);
        Assert.Equal(CacheCleanupItemState.PurgePending, pending.State);
        Assert.False(pending.CanRestore);
        CacheCleanupOperationResult restore = await service.RestoreAsync(pending);
        Assert.False(restore.Success);
        Assert.Contains("purge", restore.Error, StringComparison.OrdinalIgnoreCase);

        CacheCleanupOperationResult retry = await service.PurgeAsync(pending);

        Assert.True(retry.Success);
        Assert.Empty((await service.DiscoverItemsAsync([location])).Items);
    }

    [Fact]
    public async Task PurgeAsync_PreCancelledRequestRemainsRecoverable()
    {
        string source = CreateCache("pre-cancel-purge");
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(location);
        Assert.True((await service.CleanupAsync(plan)).Success);
        CacheCleanupItem item = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        CacheCleanupOperationResult result = await service.PurgeAsync(
            item,
            cancellationToken: cancellation.Token);
        CacheCleanupItem recoverable = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.False(result.PurgePending);
        Assert.True(result.RecoveryAvailable);
        Assert.True(recoverable.CanRestore);
    }

    [Fact]
    public async Task PurgeAsync_CompletesPendingTransactionWhenContentDirectoryIsGone()
    {
        string source = CreateCache("missing-purge-content");
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(location);
        Assert.True((await service.CleanupAsync(plan)).Success);
        CacheCleanupItem item = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(item.ManifestPath))!.AsObject();
        manifest["state"] = "purge-pending";
        manifest["contentEntryNames"] = new JsonArray();
        File.WriteAllText(
            item.ManifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Directory.Delete(Path.Combine(item.TrashPath, "content"), recursive: true);
        CacheCleanupItem pending = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);

        CacheCleanupOperationResult result = await service.PurgeAsync(pending);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(item.TrashPath));
        Assert.Empty((await service.DiscoverItemsAsync([location])).Items);
    }

    [Fact]
    public async Task DiscoverItemsAsync_KeepsPurgeAvailableWhenEmptySourceDirectoryIsDeleted()
    {
        string source = CreateCache("deleted-source");
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(location);
        Assert.True((await service.CleanupAsync(plan)).Success);
        Directory.Delete(source, recursive: false);

        CacheCleanupItem item = Assert.Single(
            (await service.DiscoverItemsAsync([location with { Exists = false }])).Items);

        Assert.False(item.CanRestore);
        Assert.True(item.CanPurge);
        Assert.NotNull(item.RestoreBlockedReason);
        CacheCleanupOperationResult result = await service.PurgeAsync(item);
        Assert.True(result.Success);
        Assert.False(Directory.Exists(item.TrashPath));
    }

    [Theory]
    [InlineData("gradle")]
    [InlineData("conan")]
    public async Task CreatePlanAsync_RefusesDefinitionsThatContainConfiguration(string cacheId)
    {
        string source = CreateCache(cacheId);
        CacheDirectoryLocation location = CreateLocation(source, cacheId);

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => new CacheCleanupService().CreatePlanAsync(location));

        Assert.Contains("configuration", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(location.Definition.SupportsSafeCleanup);
        Assert.True(location.Definition.SupportsMigration);
    }

    [Theory]
    [InlineData("pip")]
    [InlineData("npm")]
    [InlineData("pnpm")]
    [InlineData("yarn")]
    [InlineData("nuget")]
    [InlineData("nuget-http")]
    [InlineData("nuget-plugins")]
    [InlineData("maven")]
    [InlineData("vcpkg")]
    public void Definitions_AllowOnlyAuditedCacheDirectories(string cacheId)
    {
        CacheDirectoryDefinition definition = CacheDirectoryService.Definitions.Single(
            item => item.Id == cacheId);

        Assert.True(definition.SupportsSafeCleanup);
    }

    [Fact]
    public async Task CreatePlanAsync_RefusesFileSystemRoot()
    {
        string root = Path.GetPathRoot(_root)!;

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new CacheCleanupService().CreatePlanAsync(CreateLocation(root)));

        Assert.Contains("root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlanAsync_RefusesProtectedUserProfileRoot()
    {
        string userProfile = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return;
        }

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new CacheCleanupService().CreatePlanAsync(CreateLocation(userProfile)));

        Assert.Contains("protected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlanAsync_RefusesCacheNestedInsideManagedRoot()
    {
        string managedRoot = Directory.CreateDirectory(Path.Combine(_root, "managed")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(managedRoot, "cache")).FullName;
        File.WriteAllText(Path.Combine(source, "cache.bin"), "cache");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new CacheCleanupService(managedRoot).CreatePlanAsync(CreateLocation(source)));

        Assert.Contains("managed root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlanAsync_RefusesConfigurationFileInsideCache()
    {
        string source = CreateCache();
        CacheDirectoryLocation location = CreateLocation(source) with
        {
            ConfigurationFilePath = Path.Combine(source, "settings.xml"),
        };

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new CacheCleanupService().CreatePlanAsync(location));

        Assert.Contains("configuration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlanAsync_RefusesDirectoryContainingMigrationSnapshots()
    {
        string managedRoot = Directory.CreateDirectory(Path.Combine(_root, "managed-root")).FullName;
        File.WriteAllText(Path.Combine(managedRoot, "cache.bin"), "cache");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new CacheCleanupService(managedRoot).CreatePlanAsync(CreateLocation(managedRoot)));

        Assert.Contains("managed root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanupAsync_RejectsPlanFromAnotherServiceInstance()
    {
        string source = CreateCache();
        CacheCleanupPlan plan = await new CacheCleanupService().CreatePlanAsync(
            CreateLocation(source));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CacheCleanupService().CleanupAsync(plan));

        Assert.Contains("service instance", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(source, "one.txt")));
    }

    [Fact]
    public async Task RestoreAndPurge_RejectTamperedManifestIdentity()
    {
        string source = CreateCache();
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(location);
        Assert.True((await service.CleanupAsync(plan)).Success);
        CacheCleanupItem item = Assert.Single(
            (await service.DiscoverItemsAsync([location])).Items);
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(item.ManifestPath))!.AsObject();
        manifest["sourcePath"] = Path.Combine(_root, "other-cache");
        File.WriteAllText(
            item.ManifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        CacheCleanupOperationResult restore = await service.RestoreAsync(item);
        CacheCleanupOperationResult purge = await service.PurgeAsync(item);
        CacheCleanupCatalog catalog = await service.DiscoverItemsAsync([location]);

        Assert.False(restore.Success);
        Assert.False(purge.Success);
        Assert.Empty(catalog.Items);
        Assert.NotEmpty(catalog.Errors);
        Assert.True(Directory.Exists(Path.Combine(item.TrashPath, "content")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(source));
    }

    [Fact]
    public async Task CleanupAsync_OccupiedTopLevelFileLeavesSourceConsistentOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string source = Directory.CreateDirectory(Path.Combine(_root, "occupied-source")).FullName;
        string occupiedPath = Path.Combine(source, "occupied.bin");
        File.WriteAllText(occupiedPath, "occupied");
        CacheDirectoryLocation location = CreateLocation(source);
        CacheCleanupService service = new();
        CacheCleanupPlan plan = await service.CreatePlanAsync(location);
        using FileStream occupied = new(
            occupiedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        CacheCleanupOperationResult result = await service.CleanupAsync(plan);

        Assert.False(result.Success);
        Assert.True(File.Exists(occupiedPath));
        Assert.False(result.RecoveryAvailable);
        Assert.Empty((await service.DiscoverItemsAsync([location])).Items);
    }

    [Fact]
    public async Task CreatePlanAsync_RefusesNestedReparsePointWhenSupported()
    {
        string source = CreateCache();
        string external = Directory.CreateDirectory(Path.Combine(_root, "external")).FullName;
        string link = Path.Combine(source, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, external);
        }
        catch (Exception linkException) when (linkException is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new CacheCleanupService().CreatePlanAsync(CreateLocation(source)));

        Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlanAsync_RefusesAncestorReparsePointWhenSupported()
    {
        string realParent = Directory.CreateDirectory(Path.Combine(_root, "real-parent")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(realParent, "cache")).FullName;
        File.WriteAllText(Path.Combine(source, "cache.bin"), "cache");
        string linkedParent = Path.Combine(_root, "linked-parent");
        try
        {
            Directory.CreateSymbolicLink(linkedParent, realParent);
        }
        catch (Exception linkException) when (linkException is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        string linkedSource = Path.Combine(linkedParent, "cache");
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new CacheCleanupService().CreatePlanAsync(CreateLocation(linkedSource)));

        Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("cache", File.ReadAllText(Path.Combine(source, "cache.bin")));
    }

    [Fact]
    public async Task CreatePlanAsync_RefusesCacheContainingAnotherIsolationRoot()
    {
        string parentCache = Directory.CreateDirectory(Path.Combine(_root, "parent-cache")).FullName;
        File.WriteAllText(Path.Combine(parentCache, "parent.bin"), "parent");
        string nestedCache = Directory.CreateDirectory(Path.Combine(parentCache, "nested-cache")).FullName;
        File.WriteAllText(Path.Combine(nestedCache, "nested.bin"), "nested");
        CacheCleanupService service = new();
        CacheCleanupPlan nestedPlan = await service.CreatePlanAsync(CreateLocation(nestedCache));
        Assert.True((await service.CleanupAsync(nestedPlan)).Success);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.CreatePlanAsync(CreateLocation(parentCache)));

        Assert.Contains("isolation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(nestedPlan.TrashPath));
        Assert.Equal("parent", File.ReadAllText(Path.Combine(parentCache, "parent.bin")));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFileSystemEntries(
                     _root,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderByDescending(path => path.Length))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        Directory.Delete(path);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private string CreateCache(string suffix = "source")
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, suffix, "nested")).Parent!.FullName;
        File.WriteAllText(Path.Combine(source, "one.txt"), "first");
        File.WriteAllText(Path.Combine(source, "nested", "two.txt"), "second");
        return source;
    }

    private static CacheDirectoryLocation CreateLocation(string source, string cacheId = "pip")
    {
        CacheDirectoryDefinition definition = CacheDirectoryService.Definitions.Single(
            item => item.Id == cacheId);
        return new CacheDirectoryLocation(
            definition,
            source,
            "test",
            Directory.Exists(source),
            ConfigurationValueKnown: true);
    }

    private sealed class ImmediateProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
