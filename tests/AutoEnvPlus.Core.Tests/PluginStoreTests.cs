using System.Text.Json;
using AutoEnvPlus.Core.Plugins;

namespace AutoEnvPlus.Core.Tests;

public sealed class PluginStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-PluginStore-{Guid.NewGuid():N}");
    private readonly List<string> _additionalCleanupRoots = [];

    [Fact]
    public async Task PreviewAndImportAsync_NormalizesCopiesAndDefaultsToDisabled()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);

        RuntimeProviderPluginImportPreview preview = await store.PreviewImportAsync(source);
        RuntimeProviderPluginDescriptor imported = await store.ImportAsync(preview);
        RuntimeProviderPluginListResult listed = await store.ListAsync();

        Assert.Equal("community-python", preview.Manifest.Id);
        Assert.Equal(Path.GetFullPath(source), preview.SourcePath);
        Assert.Equal(1, preview.ReleaseCount);
        Assert.Equal(1, preview.AssetCount);
        Assert.Equal(["downloads.example"], preview.DownloadHosts);
        Assert.Equal([AutoEnvPlus.Core.Providers.PackageHashAlgorithm.Sha256], preview.HashAlgorithms);
        Assert.Equal(
            Path.Combine(store.PluginRoot, "community-python.json"),
            imported.ManifestPath);
        Assert.True(File.Exists(imported.ManifestPath));
        Assert.False(imported.IsEnabled);
        Assert.False(File.Exists(store.StatePath));
        RuntimeProviderPluginDescriptor listedPlugin = Assert.Single(listed.Plugins);
        Assert.False(listedPlugin.IsEnabled);
        Assert.Empty(listed.Errors);
        string normalizedJson = await File.ReadAllTextAsync(imported.ManifestPath);
        Assert.Contains("\"schemaVersion\": 2", normalizedJson, StringComparison.Ordinal);
        Assert.Contains("\"languageToolId\": \"cpython\"", normalizedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimeKind", normalizedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("source.json", normalizedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnableDisableAsync_SavesSeparateAtomicStateAndAcceptsProviderId()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);
        await store.ImportAsync(source);

        RuntimeProviderPluginDescriptor enabled = await store.EnableAsync(
            "plugin:community-python");
        RuntimeProviderPluginListResult enabledList = await store.ListAsync();
        RuntimeProviderPluginDescriptor disabled = await store.DisableAsync(
            "community-python");
        RuntimeProviderPluginListResult disabledList = await store.ListAsync();

        Assert.True(enabled.IsEnabled);
        Assert.True(Assert.Single(enabledList.Plugins).IsEnabled);
        Assert.Equal(1, enabledList.EnabledCount);
        Assert.False(disabled.IsEnabled);
        Assert.False(Assert.Single(disabledList.Plugins).IsEnabled);
        Assert.Equal(0, disabledList.EnabledCount);
        using JsonDocument state = JsonDocument.Parse(
            await File.ReadAllTextAsync(store.StatePath));
        Assert.Equal(
            RuntimeProviderPluginStore.CurrentStateSchemaVersion,
            state.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(state.RootElement.GetProperty("enabledPluginIds").EnumerateArray());
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(store.StatePath)!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ImportAsync_DuplicateIdLeavesOriginalManifestUntouched()
    {
        string sources = Path.Combine(_root, "sources");
        string first = await PluginTestData.WriteManifestAsync(sources);
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor original = await store.ImportAsync(first);
        byte[] before = await File.ReadAllBytesAsync(original.ManifestPath);
        string replacement = Path.Combine(sources, "replacement.json");
        await File.WriteAllTextAsync(
            replacement,
            PluginTestData.CreateManifestNode().ToJsonString());

        RuntimeProviderPluginException exception = await Assert.ThrowsAsync<
            RuntimeProviderPluginException>(() => store.ImportAsync(replacement));
        byte[] after = await File.ReadAllBytesAsync(original.ManifestPath);

        Assert.Equal(RuntimeProviderPluginErrorCode.DuplicatePlugin, exception.Code);
        Assert.Equal(before, after);
        Assert.Empty(Directory.EnumerateFiles(
            store.PluginRoot,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ImportAsync_InvalidManifestDoesNotCreateManagedPluginFile()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"),
            mutate: document => PluginTestData.FirstAsset(document)["downloadUri"] =
                "https://user:secret@downloads.example/python.zip");
        RuntimeProviderPluginStore store = new(_root);

        RuntimeProviderPluginException exception = await Assert.ThrowsAsync<
            RuntimeProviderPluginException>(() => store.ImportAsync(source));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.False(Directory.Exists(store.PluginRoot));
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_ReturnsValidPluginsAndSafeErrorForCorruptDisabledManifest()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor imported = await store.ImportAsync(source);
        await File.WriteAllTextAsync(
            Path.Combine(store.PluginRoot, "broken-plugin.json"),
            "{\"downloadUri\":\"https://user:secret@example.test/archive.zip\"");

        RuntimeProviderPluginListResult result = await store.ListAsync();
        string diagnostics = result.ToString()
            + string.Join(System.Environment.NewLine, result.Errors);

        Assert.Single(result.Plugins);
        Assert.Equal(imported.Id, result.Plugins[0].Id);
        RuntimeProviderPluginError error = Assert.Single(result.Errors);
        Assert.Equal(RuntimeProviderPluginErrorCode.MalformedJson, error.Code);
        Assert.Equal("broken-plugin", error.PluginId);
        Assert.Equal("broken-plugin.json", error.FileName);
        Assert.False(error.IsEnabled);
        Assert.DoesNotContain("secret", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListAsync_EnabledCorruptManifestIsMarkedForFailClosedRegistry()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor imported = await store.ImportAsync(source);
        await store.EnableAsync(imported.Id);
        await File.WriteAllTextAsync(imported.ManifestPath, "{broken");

        RuntimeProviderPluginListResult result = await store.ListAsync();

        Assert.Empty(result.Plugins);
        Assert.Contains(result.Errors, error =>
            error.PluginId == imported.Id && error.IsEnabled);
    }

    [Fact]
    public async Task ListAsync_FutureStateSchemaReturnsSafeStateError()
    {
        RuntimeProviderPluginStore store = new(_root);
        _ = await store.ListAsync();
        await File.WriteAllTextAsync(
            store.StatePath,
            "{\"schemaVersion\":999,\"enabledPluginIds\":[]}");

        RuntimeProviderPluginListResult result = await store.ListAsync();

        RuntimeProviderPluginError error = Assert.Single(result.Errors);
        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidState, error.Code);
        Assert.Null(error.PluginId);
        Assert.Equal(Path.GetFileName(store.StatePath), error.FileName);
    }

    [Fact]
    public async Task ConcurrentStores_ImportDistinctPluginsWithoutPartialFiles()
    {
        RuntimeProviderPluginStore[] stores = Enumerable.Range(0, 4)
            .Select(_ => new RuntimeProviderPluginStore(_root))
            .ToArray();
        string sources = Path.Combine(_root, "sources");
        string[] sourcePaths = await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            PluginTestData.WriteManifestAsync(
                sources,
                $"community-python-{index}")));

        RuntimeProviderPluginDescriptor[] imports = await Task.WhenAll(
            sourcePaths.Select((source, index) => stores[index % stores.Length].ImportAsync(source)));
        RuntimeProviderPluginListResult result = await stores[0].ListAsync();

        Assert.Equal(20, imports.Length);
        Assert.Equal(20, result.Plugins.Count);
        Assert.Equal(20, result.Plugins.Select(plugin => plugin.Id).Distinct().Count());
        Assert.Empty(result.Errors);
        Assert.Empty(Directory.EnumerateFiles(
            stores[0].PluginRoot,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
        Assert.All(result.Plugins, plugin =>
            RuntimeProviderPluginManifestParser.Parse(File.ReadAllBytes(plugin.ManifestPath)));
    }

    [Fact]
    public async Task DeleteAsync_RemovesEnabledPluginAndActivationState()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor imported = await store.ImportAsync(source);
        await store.EnableAsync(imported.Id);

        RuntimeProviderPluginDeleteResult deleted = await store.DeleteAsync(
            "plugin:community-python");
        RuntimeProviderPluginListResult result = await store.ListAsync();

        Assert.True(deleted.Success);
        Assert.Equal(RuntimeProviderPluginDeleteOutcome.Deleted, deleted.Outcome);
        Assert.True(deleted.WasEnabled);
        Assert.False(deleted.CleanupPending);
        Assert.Null(deleted.QuarantinePath);
        Assert.False(File.Exists(imported.ManifestPath));
        Assert.Empty(result.Plugins);
        Assert.Empty(result.Errors);
        using JsonDocument state = JsonDocument.Parse(
            await File.ReadAllTextAsync(store.StatePath));
        Assert.Empty(state.RootElement.GetProperty("enabledPluginIds").EnumerateArray());
    }

    [Fact]
    public async Task DeleteAsync_ReadOnlyManifestReturnsExplicitQuarantineResult()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor imported = await store.ImportAsync(source);
        File.SetAttributes(imported.ManifestPath, FileAttributes.ReadOnly);

        RuntimeProviderPluginDeleteResult deleted = await store.DeleteAsync(imported.Id);

        Assert.Equal(
            RuntimeProviderPluginDeleteOutcome.DeletedWithQuarantinedCopy,
            deleted.Outcome);
        Assert.True(deleted.CleanupPending);
        Assert.NotNull(deleted.QuarantinePath);
        Assert.True(File.Exists(deleted.QuarantinePath));
        Assert.False(File.Exists(imported.ManifestPath));
        File.SetAttributes(deleted.QuarantinePath!, FileAttributes.Normal);
    }

    [Fact]
    public async Task DeleteAsync_StateSaveFailureRollsManifestBack()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor imported = await store.ImportAsync(source);
        await store.EnableAsync(imported.Id);
        File.SetAttributes(store.StatePath, FileAttributes.ReadOnly);

        RuntimeProviderPluginException exception = await Assert.ThrowsAsync<
            RuntimeProviderPluginException>(() => store.DeleteAsync(imported.Id));

        Assert.Equal(RuntimeProviderPluginErrorCode.IoFailure, exception.Code);
        Assert.True(File.Exists(imported.ManifestPath));
        File.SetAttributes(store.StatePath, FileAttributes.Normal);
        RuntimeProviderPluginListResult result = await store.ListAsync();
        Assert.True(Assert.Single(result.Plugins).IsEnabled);
    }

    [Fact]
    public async Task DeleteAsync_CorruptEnabledManifestCanBeRemovedSafely()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor imported = await store.ImportAsync(source);
        await store.EnableAsync(imported.Id);
        await File.WriteAllTextAsync(imported.ManifestPath, "{broken");

        RuntimeProviderPluginDeleteResult deleted = await store.DeleteAsync(imported.Id);
        RuntimeProviderPluginListResult result = await store.ListAsync();

        Assert.Equal(RuntimeProviderPluginDeleteOutcome.Deleted, deleted.Outcome);
        Assert.True(deleted.WasEnabled);
        Assert.False(File.Exists(imported.ManifestPath));
        Assert.Empty(result.Plugins);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task DeleteAsync_MissingEnabledManifestRepairsStaleActivationState()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor imported = await store.ImportAsync(source);
        await store.EnableAsync(imported.Id);
        File.Delete(imported.ManifestPath);

        RuntimeProviderPluginDeleteResult deleted = await store.DeleteAsync(imported.Id);
        RuntimeProviderPluginListResult result = await store.ListAsync();

        Assert.Equal(RuntimeProviderPluginDeleteOutcome.Deleted, deleted.Outcome);
        Assert.True(deleted.WasEnabled);
        Assert.Empty(result.Plugins);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task PreviewImportAsync_RejectsSymbolicLinkSource()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        string link = Path.Combine(_root, "linked-source.json");
        try
        {
            File.CreateSymbolicLink(link, source);
        }
        catch (Exception linkException) when (linkException is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        RuntimeProviderPluginException exception = await Assert.ThrowsAsync<
            RuntimeProviderPluginException>(() =>
                new RuntimeProviderPluginStore(_root).PreviewImportAsync(link));

        Assert.Equal(RuntimeProviderPluginErrorCode.UnsafePath, exception.Code);
    }

    [Fact]
    public async Task ListAsync_RejectsManagedPluginRootReparsePoint()
    {
        string target = Path.Combine(
            Path.GetTempPath(),
            $"AutoEnvPlus-PluginTarget-{Guid.NewGuid():N}");
        _additionalCleanupRoots.Add(target);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        string link = Path.Combine(_root, "plugins", "runtime-providers");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        RuntimeProviderPluginListResult result = await new RuntimeProviderPluginStore(_root)
            .ListAsync();

        RuntimeProviderPluginError error = Assert.Single(result.Errors);
        Assert.Equal(RuntimeProviderPluginErrorCode.UnsafePath, error.Code);
    }

    [Fact]
    public async Task ListAsync_RejectsImportedManifestReparsePoint()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"));
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor imported = await store.ImportAsync(source);
        string external = Path.Combine(_root, "external.json");
        await File.WriteAllTextAsync(external, PluginTestData.CreateManifestNode().ToJsonString());
        File.Delete(imported.ManifestPath);
        try
        {
            File.CreateSymbolicLink(imported.ManifestPath, external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        RuntimeProviderPluginListResult result = await store.ListAsync();

        RuntimeProviderPluginError error = Assert.Single(result.Errors);
        Assert.Equal(RuntimeProviderPluginErrorCode.UnsafePath, error.Code);
        Assert.Equal(imported.Id, error.PluginId);
    }

    [Fact]
    public async Task PreviewImportAsync_RejectsOversizedFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sources"));
        string source = Path.Combine(_root, "sources", "large.json");
        await File.WriteAllBytesAsync(
            source,
            new byte[RuntimeProviderPluginManifestParser.MaximumManifestBytes + 1]);

        RuntimeProviderPluginException exception = await Assert.ThrowsAsync<
            RuntimeProviderPluginException>(() =>
                new RuntimeProviderPluginStore(_root).PreviewImportAsync(source));

        Assert.Equal(RuntimeProviderPluginErrorCode.ManifestTooLarge, exception.Code);
    }

    [Fact]
    public async Task Operations_RejectUnknownPluginWithoutWritingState()
    {
        RuntimeProviderPluginStore store = new(_root);

        RuntimeProviderPluginException enable = await Assert.ThrowsAsync<
            RuntimeProviderPluginException>(() => store.EnableAsync("missing-plugin"));
        RuntimeProviderPluginException delete = await Assert.ThrowsAsync<
            RuntimeProviderPluginException>(() => store.DeleteAsync("missing-plugin"));

        Assert.Equal(RuntimeProviderPluginErrorCode.PluginNotFound, enable.Code);
        Assert.Equal(RuntimeProviderPluginErrorCode.PluginNotFound, delete.Code);
        Assert.False(File.Exists(store.StatePath));
    }

    public void Dispose()
    {
        DeleteTree(_root);
        foreach (string path in _additionalCleanupRoots)
        {
            DeleteTree(path);
        }
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(
                     path,
                     "*",
                     SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (FileNotFoundException)
            {
            }
        }

        Directory.Delete(path, recursive: true);
    }
}
