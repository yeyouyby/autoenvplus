using System.Text.Json;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class GlobalRuntimeProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Profile-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetAsync_PreservesOtherSelectionsAcrossInstances()
    {
        GlobalRuntimeProfileStore first = new(_root);
        await first.SetAsync(RuntimeKind.Python, VersionSelector.Parse("3.13"));
        await first.SetAsync(RuntimeKind.NodeJs, VersionSelector.Parse("24-lts"));

        RuntimeProfile loaded = await new GlobalRuntimeProfileStore(_root).LoadAsync();

        Assert.Equal(VersionSelector.Parse("3.13"), loaded.Selections[RuntimeKind.Python]);
        Assert.Equal(VersionSelector.Parse("24-lts"), loaded.Selections[RuntimeKind.NodeJs]);
    }

    [Fact]
    public async Task SetExactAsync_RoundTripsRuntimeAndProviderIdentity()
    {
        GlobalRuntimeProfileStore store = new(_root);

        await store.SetExactAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13.5"),
            "plugin-community-python-3.13.5-x64",
            "plugin:community-python");
        RuntimeProfile loaded = await new GlobalRuntimeProfileStore(_root).LoadAsync();

        Assert.Equal(VersionSelector.Parse("3.13.5"), loaded.Selections[RuntimeKind.Python]);
        RuntimeSelectionIdentity identity = loaded.ExactSelections[RuntimeKind.Python];
        Assert.Equal("plugin-community-python-3.13.5-x64", identity.RuntimeId);
        Assert.Equal("plugin:community-python", identity.ProviderId);
        string json = await File.ReadAllTextAsync(store.ProfilePath);
        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"exactSelections\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_ReplacesSelectorAndClearsPreviousExactIdentity()
    {
        GlobalRuntimeProfileStore store = new(_root);
        await store.SetExactAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13.5"),
            "python-3.13.5-x64",
            "python-org");

        RuntimeProfile updated = await store.SetAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.14"));

        Assert.Equal(VersionSelector.Parse("3.14"), updated.Selections[RuntimeKind.Python]);
        Assert.False(updated.ExactSelections.ContainsKey(RuntimeKind.Python));
    }

    [Fact]
    public async Task LoadAsync_ReadsLegacySchemaOneWithoutIdentityPins()
    {
        GlobalRuntimeProfileStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.ProfilePath)!);
        await File.WriteAllTextAsync(
            store.ProfilePath,
            "{\"schemaVersion\":1,\"selections\":{\"Python\":\"3.12\"}}");

        RuntimeProfile loaded = await store.LoadAsync();

        Assert.Equal(VersionSelector.Parse("3.12"), loaded.Selections[RuntimeKind.Python]);
        Assert.Empty(loaded.ExactSelections);
    }

    [Fact]
    public async Task LoadAsync_RejectsExactIdentityWithoutMatchingSelector()
    {
        GlobalRuntimeProfileStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.ProfilePath)!);
        await File.WriteAllTextAsync(
            store.ProfilePath,
            "{\"schemaVersion\":2,\"selections\":{},\"exactSelections\":{\"Python\":{\"runtimeId\":\"python-x64\",\"providerId\":\"python-org\"}}}");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync());

        Assert.Contains("matching version selector", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetAsync_TwoStoreInstancesDoNotLoseConcurrentSelections()
    {
        RuntimeKind[] kinds = Enum.GetValues<RuntimeKind>();
        GlobalRuntimeProfileStore[] stores = kinds
            .Select(_ => new GlobalRuntimeProfileStore(_root))
            .ToArray();

        await Task.WhenAll(stores.Select((store, index) => store.SetAsync(
            kinds[index],
            VersionSelector.Parse($"{index + 1}.0"))));
        RuntimeProfile loaded = await new GlobalRuntimeProfileStore(_root).LoadAsync();

        Assert.Equal(kinds.Length, loaded.Selections.Count);
        Assert.All(kinds, kind => Assert.True(loaded.Selections.ContainsKey(kind)));
    }

    [Fact]
    public async Task SetAsync_WaitsForFixedInterprocessLock()
    {
        GlobalRuntimeProfileStore first = new(_root);
        GlobalRuntimeProfileStore second = new(_root);
        await first.LoadAsync();
        string lockPath = Path.Combine(
            _root,
            "state",
            "global-runtime-profile.lock");
        Task<RuntimeProfile> pendingSet;

        using (FileStream heldLock = new(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            pendingSet = second.SetAsync(
                RuntimeKind.Python,
                VersionSelector.Parse("3.13"));
            await Task.Delay(150);
            Assert.False(pendingSet.IsCompleted);
        }

        RuntimeProfile result = await pendingSet;
        Assert.Equal(VersionSelector.Parse("3.13"), result.Selections[RuntimeKind.Python]);
    }

    [Fact]
    public async Task LoadAsync_MissingProfileReturnsEmpty()
    {
        RuntimeProfile loaded = await new GlobalRuntimeProfileStore(_root).LoadAsync();

        Assert.Empty(loaded.Selections);
    }

    [Fact]
    public async Task LoadAsync_RejectsProfileAboveSizeLimitBeforeDeserialization()
    {
        GlobalRuntimeProfileStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.ProfilePath)!);
        await using (FileStream stream = new(
            store.ProfilePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            stream.SetLength(GlobalRuntimeProfileStore.MaximumProfileBytes + 1);
        }

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.LoadAsync());

        Assert.Contains("byte limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RejectsProfileAboveSelectionLimitBeforeSelectionValidation()
    {
        GlobalRuntimeProfileStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.ProfilePath)!);
        Dictionary<string, string> selections = Enumerable.Range(
                0,
                GlobalRuntimeProfileStore.MaximumProfileSelections + 1)
            .ToDictionary(index => $"future-runtime-{index}", _ => "1.0");
        await File.WriteAllTextAsync(
            store.ProfilePath,
            JsonSerializer.Serialize(new { schemaVersion = 1, selections }));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.LoadAsync());

        Assert.Contains("selection limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RejectsReparsePointProfileFileWhenSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GlobalRuntimeProfileStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.ProfilePath)!);
        string externalProfile = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Profile-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            externalProfile,
            "{\"schemaVersion\":1,\"selections\":{}}");
        try
        {
            try
            {
                File.CreateSymbolicLink(store.ProfilePath, externalProfile);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            IOException unsafePathException = await Assert.ThrowsAnyAsync<IOException>(() =>
                store.LoadAsync());

            Assert.Contains(
                "reparse",
                unsafePathException.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(store.ProfilePath))
            {
                File.Delete(store.ProfilePath);
            }

            if (File.Exists(externalProfile))
            {
                File.Delete(externalProfile);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_RejectsReparsePointLockFileWhenSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GlobalRuntimeProfileStore store = new(_root);
        string stateDirectory = Path.GetDirectoryName(store.ProfilePath)!;
        Directory.CreateDirectory(stateDirectory);
        string lockPath = Path.Combine(stateDirectory, "global-runtime-profile.lock");
        string externalLock = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Profile-Lock-{Guid.NewGuid():N}.lock");
        await File.WriteAllTextAsync(externalLock, "lock");
        try
        {
            try
            {
                File.CreateSymbolicLink(lockPath, externalLock);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            IOException unsafePathException = await Assert.ThrowsAnyAsync<IOException>(() =>
                store.LoadAsync());

            Assert.Contains(
                "reparse",
                unsafePathException.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
            }

            if (File.Exists(externalLock))
            {
                File.Delete(externalLock);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
