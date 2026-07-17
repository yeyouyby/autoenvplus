using System.Text.Json;
using AutoEnvPlus.Core.Languages;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProviderSourcePreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-ProviderSources-{Guid.NewGuid():N}");

    [Fact]
    public async Task BuiltInOverride_RoundTripsResolvesAndRestoresCatalogDefault()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        ProviderSourcePreferenceStore store = new(_root);
        ProviderSourceOwner owner = PyPiOwner();

        await store.SetBuiltInOverrideAsync(
            catalog,
            owner,
            "https://mirror.example/simple");
        ProviderSourceResolutionResult overridden = await store.ResolveAsync(catalog, owner);

        Assert.True(overridden.Success);
        Assert.Equal(ProviderSourceOrigin.UserOverride, overridden.Source!.Origin);
        Assert.Equal("https://mirror.example/simple", overridden.Source.EffectiveEndpoint!.AbsoluteUri);
        Assert.Single((await store.LoadValidatedAsync(catalog)).Overrides);

        await store.RestoreBuiltInDefaultAsync(catalog, owner);
        ProviderSourceResolutionResult restored = await store.ResolveAsync(catalog, owner);

        Assert.True(restored.Success);
        Assert.Equal(ProviderSourceOrigin.CatalogDefault, restored.Source!.Origin);
        Assert.Equal("https://pypi.org/simple", restored.Source.EffectiveEndpoint!.AbsoluteUri);
        Assert.Empty((await store.LoadValidatedAsync(catalog)).Overrides);
        string json = await File.ReadAllTextAsync(store.PreferencesPath);
        Assert.DoesNotContain("proxy", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomSource_AddDisableEnableResolveProviderAndDelete()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        ProviderSourcePreferenceStore store = new(_root);
        ProviderSourceOwner owner = CustomOwner("company-pypi");

        await store.AddCustomSourceAsync(
            catalog,
            owner,
            "Company PyPI",
            "https://packages.example/simple",
            ProviderMirrorEndpointKind.PyPi,
            "Company Python packages");
        ProviderSourceResolutionResult enabled = await store.ResolveAsync(catalog, owner);
        Assert.True(enabled.Success);
        Assert.Equal(ProviderSourceOrigin.Custom, enabled.Source!.Origin);
        Assert.Equal("https://packages.example/simple", enabled.Source.EffectiveEndpoint!.AbsoluteUri);

        await store.SetCustomSourceEnabledAsync(catalog, owner, enabled: false);
        ProviderSourceResolutionResult disabled = await store.ResolveAsync(catalog, owner);
        Assert.True(disabled.Success);
        Assert.False(disabled.Source!.IsEnabled);
        Assert.Null(disabled.Source.EffectiveEndpoint);
        Assert.Equal("https://packages.example/simple", disabled.Source.ConfiguredEndpoint.AbsoluteUri);

        await store.SetCustomSourceEnabledAsync(catalog, owner, enabled: true);
        ProviderSourceListResolutionResult provider = await store.ResolveProviderAsync(
            catalog,
            "cpython",
            "official-archive");
        Assert.True(provider.Success);
        Assert.Contains(provider.Sources, source =>
            source.Owner == owner && source.EffectiveEndpoint is not null);

        await store.DeleteCustomSourceAsync(catalog, owner);
        Assert.Empty((await store.LoadValidatedAsync(catalog)).CustomSources);
        ProviderSourceResolutionResult deleted = await store.ResolveAsync(catalog, owner);
        Assert.False(deleted.Success);
        Assert.Equal(
            ProviderSourcePreferenceErrorCode.CatalogSlotNotFound,
            Assert.Single(deleted.Errors).Code);
    }

    [Fact]
    public async Task EnablingCustomSource_AtomicallyDisablesSameKindButKeepsOtherKindsEnabled()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        ProviderSourcePreferenceStore store = new(_root);
        ProviderSourceOwner first = CustomOwner("first-pypi");
        ProviderSourceOwner second = CustomOwner("second-pypi");
        ProviderSourceOwner downloads = CustomOwner("company-downloads");
        await store.AddCustomSourceAsync(
            catalog,
            first,
            "First PyPI",
            "https://first.example/simple",
            ProviderMirrorEndpointKind.PyPi,
            "First Python source");
        await store.AddCustomSourceAsync(
            catalog,
            downloads,
            "Company downloads",
            "https://downloads.example/python",
            ProviderMirrorEndpointKind.GenericDownload,
            "Company Python downloads");
        await store.AddCustomSourceAsync(
            catalog,
            second,
            "Second PyPI",
            "https://second.example/simple",
            ProviderMirrorEndpointKind.PyPi,
            "Second Python source");

        ProviderSourcePreferences added = await store.LoadValidatedAsync(catalog);
        Assert.False(Assert.Single(added.CustomSources, item => item.Owner == first).IsEnabled);
        Assert.True(Assert.Single(added.CustomSources, item => item.Owner == second).IsEnabled);
        Assert.True(Assert.Single(added.CustomSources, item => item.Owner == downloads).IsEnabled);

        await store.SetCustomSourceEnabledAsync(catalog, first, enabled: true);
        ProviderSourcePreferences switched = await store.LoadValidatedAsync(catalog);
        Assert.True(Assert.Single(switched.CustomSources, item => item.Owner == first).IsEnabled);
        Assert.False(Assert.Single(switched.CustomSources, item => item.Owner == second).IsEnabled);
        Assert.True(Assert.Single(switched.CustomSources, item => item.Owner == downloads).IsEnabled);
    }

    [Theory]
    [InlineData("http://mirror.example/simple")]
    [InlineData("https://user:secret@mirror.example/simple")]
    [InlineData("https://mirror.example/simple?token=secret")]
    [InlineData("https://mirror.example/simple#fragment")]
    public async Task Mutations_RejectNonHttpsOrSensitiveEndpointsWithoutChangingState(
        string endpoint)
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        ProviderSourcePreferenceStore store = new(_root);
        await store.SetBuiltInOverrideAsync(
            catalog,
            PyPiOwner(),
            "https://original.example/simple");
        byte[] before = await File.ReadAllBytesAsync(store.PreferencesPath);

        ProviderSourcePreferenceException endpointError = await Assert.ThrowsAsync<
            ProviderSourcePreferenceException>(() => store.SetBuiltInOverrideAsync(
                catalog,
                PyPiOwner(),
                endpoint));

        Assert.Equal(ProviderSourcePreferenceErrorCode.InvalidEndpoint, endpointError.Error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.PreferencesPath));
    }

    [Fact]
    public async Task Mutations_ValidateEffectiveCatalogAndRejectBuiltInSlotCollisions()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        ProviderSourcePreferenceStore store = new(_root);
        ProviderSourceOwner missingTool = PyPiOwner() with { LanguageToolId = "missing-tool" };

        ProviderSourcePreferenceException missingToolError = await Assert.ThrowsAsync<
            ProviderSourcePreferenceException>(() => store.AddCustomSourceAsync(
                catalog,
                missingTool with { SlotId = "company-source" },
                "Company source",
                "https://packages.example/source",
                ProviderMirrorEndpointKind.GenericDownload,
                "Company packages"));
        Assert.Equal(
            ProviderSourcePreferenceErrorCode.CatalogToolNotFound,
            missingToolError.Error.Code);

        ProviderSourcePreferenceException conflictError = await Assert.ThrowsAsync<
            ProviderSourcePreferenceException>(() => store.AddCustomSourceAsync(
                catalog,
                PyPiOwner(),
                "Conflicting source",
                "https://packages.example/simple",
                ProviderMirrorEndpointKind.PyPi,
                "Must not replace the catalog slot"));
        Assert.Equal(ProviderSourcePreferenceErrorCode.SlotConflict, conflictError.Error.Code);

        LanguageCatalog lockedCatalog = CreateCatalogWithLockedPyPiSlot(catalog);
        ProviderSourcePreferenceException lockedError = await Assert.ThrowsAsync<
            ProviderSourcePreferenceException>(() => store.SetBuiltInOverrideAsync(
                lockedCatalog,
                PyPiOwner(),
                "https://mirror.example/simple"));
        Assert.Equal(
            ProviderSourcePreferenceErrorCode.SlotNotOverridable,
            lockedError.Error.Code);
    }

    [Fact]
    public async Task LoadValidatedAsync_RejectsReferencesMissingFromChangedEffectiveCatalog()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        ProviderSourcePreferenceStore store = new(_root);
        await store.AddCustomSourceAsync(
            catalog,
            CustomOwner("temporary-source"),
            "Temporary source",
            "https://packages.example/source",
            ProviderMirrorEndpointKind.GenericDownload,
            "Temporary packages");
        LanguageCatalog withoutCpython = new(
            catalog.Languages,
            catalog.Tools.Where(tool => !tool.Id.Equals(
                "cpython",
                StringComparison.OrdinalIgnoreCase)));

        ProviderSourcePreferenceException referenceError = await Assert.ThrowsAsync<
            ProviderSourcePreferenceException>(() => store.LoadValidatedAsync(withoutCpython));

        Assert.Equal(
            ProviderSourcePreferenceErrorCode.CatalogToolNotFound,
            referenceError.Error.Code);
        Assert.Single((await store.LoadAsync()).CustomSources);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"overrides\":[],\"customSources\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"overrides\":[],\"customSources\":[],\"proxy\":\"https://forbidden.example\"}")]
    [InlineData("{\"schemaVersion\":1,\"overrides\":{},\"customSources\":[]}")]
    public async Task LoadAsync_RejectsDuplicateUnknownAndWrongShapeJson(string json)
    {
        ProviderSourcePreferenceStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreferencesPath)!);
        await File.WriteAllTextAsync(store.PreferencesPath, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_RejectsOversizedDocumentBeforeParsing()
    {
        ProviderSourcePreferenceStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreferencesPath)!);
        await using (FileStream stream = new(
            store.PreferencesPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            stream.SetLength(ProviderSourcePreferenceStore.MaximumDocumentBytes + 1);
        }

        InvalidDataException sizeError = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync());
        Assert.Contains("bytes", sizeError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentStoreInstances_PreserveEveryReadModifyWriteUpdate()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        ProviderSourcePreferenceStore[] stores =
        [new(_root), new(_root)];
        Task<ProviderSourcePreferences>[] updates = Enumerable.Range(0, 24)
            .Select(index => stores[index % stores.Length].AddCustomSourceAsync(
                catalog,
                CustomOwner($"source-{index:D2}"),
                $"Source {index:D2}",
                $"https://source-{index:D2}.example/packages",
                ProviderMirrorEndpointKind.GenericDownload,
                $"Packages from source {index:D2}"))
            .ToArray();

        await Task.WhenAll(updates);
        ProviderSourcePreferences loaded = await stores[0].LoadValidatedAsync(catalog);

        Assert.Equal(24, loaded.CustomSources.Count);
        string json = await File.ReadAllTextAsync(stores[0].PreferencesPath);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(24, document.RootElement.GetProperty("customSources").GetArrayLength());
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(stores[0].PreferencesPath)!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task UpdateAsync_WaitsForTheCrossProcessStateLock()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;
        ProviderSourcePreferenceStore store = new(_root);
        _ = await store.LoadAsync();
        string lockPath = Path.Combine(
            _root,
            "state",
            "provider-source-preferences.lock");
        Task<ProviderSourcePreferences> pending;

        using (FileStream heldLock = new(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            pending = store.AddCustomSourceAsync(
                catalog,
                CustomOwner("locked-source"),
                "Locked source",
                "https://locked.example/packages",
                ProviderMirrorEndpointKind.GenericDownload,
                "Lock test packages");
            await Task.Delay(150);
            Assert.False(pending.IsCompleted);
        }

        Assert.Single((await pending).CustomSources);
    }

    [Fact]
    public async Task LoadAsync_RejectsReparsePreferenceFileWithoutReadingTargetWhenSupported()
    {
        ProviderSourcePreferenceStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreferencesPath)!);
        string external = Path.Combine(_root, "external.json");
        const string externalContent = "{\"schemaVersion\":1,\"overrides\":[],\"customSources\":[]}";
        await File.WriteAllTextAsync(external, externalContent);
        try
        {
            try
            {
                File.CreateSymbolicLink(store.PreferencesPath, external);
            }
            catch (Exception linkError) when (linkError is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            await Assert.ThrowsAnyAsync<IOException>(() => store.LoadAsync());
            Assert.Equal(externalContent, await File.ReadAllTextAsync(external));
        }
        finally
        {
            if (File.Exists(store.PreferencesPath))
            {
                File.Delete(store.PreferencesPath);
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

    private static ProviderSourceOwner PyPiOwner() => new(
        "cpython",
        "official-archive",
        "python-package-index");

    private static ProviderSourceOwner CustomOwner(string slotId) => new(
        "cpython",
        "official-archive",
        slotId);

    private static LanguageCatalog CreateCatalogWithLockedPyPiSlot(LanguageCatalog catalog)
    {
        Assert.True(catalog.TryGetTool("cpython", out LanguageToolDefinition? cpython));
        LanguageToolDefinition replacement = cpython! with
        {
            Providers = cpython.Providers.Select(provider => provider.Id == "official-archive"
                ? provider with
                {
                    MirrorSlots = provider.MirrorSlots.Select(slot =>
                        slot.Id == "python-package-index"
                            ? slot with { UserOverridable = false }
                            : slot).ToArray(),
                }
                : provider).ToArray(),
        };
        return new LanguageCatalog(
            catalog.Languages,
            catalog.Tools.Select(tool => tool.Id == replacement.Id ? replacement : tool));
    }
}
