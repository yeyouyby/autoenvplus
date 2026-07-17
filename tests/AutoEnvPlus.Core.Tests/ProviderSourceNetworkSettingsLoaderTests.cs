using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Networking;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProviderSourceNetworkSettingsLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-ProviderNetwork-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_CombinesNetworkProxyWithProviderMirrorAndBindsBothSnapshots()
    {
        NetworkSettingsSaveResult networkSaved = await new NetworkSettingsStore(_root).SaveAsync(
            new NetworkSettings(new GlobalNetworkSettings(
                HttpProxy: "http://proxy.example:8080",
                NoProxy: ["localhost"])));
        Assert.True(networkSaved.Success);
        ProviderSourcePreferenceStore sources = new(_root);
        await sources.SetBuiltInOverrideAsync(
            BuiltInLanguageCatalog.Current,
            new ProviderSourceOwner("pip", "bundled", "python-package-index"),
            "https://company.example/simple");

        ProviderSourceNetworkSettingsLoadResult loaded =
            await new ProviderSourceNetworkSettingsLoader(_root).LoadAsync();

        Assert.True(loaded.Success);
        Assert.Matches("^[0-9A-F]{64}$", loaded.NetworkSettingsSha256!);
        Assert.Matches("^[0-9A-F]{64}$", loaded.ProviderSourcePreferencesSha256!);
        Assert.Equal(sources.PreferencesPath, loaded.ProviderSourcePreferencesPath);
        NetworkSettingsResolutionResult pip = NetworkSettingsResolver.Resolve(
            loaded.Settings!,
            NetworkToolIds.Pip);
        Assert.True(pip.Success);
        Assert.Equal("http://proxy.example:8080/", pip.EffectiveSettings!.HttpProxy!.AbsoluteUri);
        Assert.Equal("https://company.example/simple", pip.EffectiveSettings.Mirror!.AbsoluteUri);
    }

    [Fact]
    public async Task LoadAsync_FailsClosedForManuallyCreatedMultipleActiveSources()
    {
        ProviderSourcePreferenceStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreferencesPath)!);
        await File.WriteAllTextAsync(
            store.PreferencesPath,
            """
            {
              "schemaVersion": 1,
              "overrides": [],
              "customSources": [
                {
                  "languageToolId": "pip",
                  "providerId": "bundled",
                  "slotId": "first-source",
                  "displayName": "First source",
                  "endpoint": "https://first.example/simple",
                  "endpointKind": "pyPi",
                  "purpose": "Python packages",
                  "enabled": true
                },
                {
                  "languageToolId": "pip",
                  "providerId": "bundled",
                  "slotId": "second-source",
                  "displayName": "Second source",
                  "endpoint": "https://second.example/simple",
                  "endpointKind": "pyPi",
                  "purpose": "Python packages",
                  "enabled": true
                }
              ]
            }
            """);

        ProviderSourceNetworkSettingsLoadResult loaded =
            await new ProviderSourceNetworkSettingsLoader(_root).LoadAsync();

        Assert.False(loaded.Success);
        Assert.Null(loaded.Settings);
        Assert.Contains(loaded.Errors, error =>
            error.Contains("Enable only one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadForToolsAsync_DoesNotProjectUnrequestedProviderSources()
    {
        ProviderSourcePreferenceStore store = new(_root);
        await store.AddCustomSourceAsync(
            BuiltInLanguageCatalog.Current,
            new ProviderSourceOwner("nuget-cli", "official-distribution", "company-nuget"),
            "Company NuGet",
            "https://nuget.example/v3/index.json",
            ProviderMirrorEndpointKind.NuGet,
            ".NET packages");

        ProviderSourceNetworkSettingsLoadResult loaded =
            await new ProviderSourceNetworkSettingsLoader(_root)
                .LoadForToolsAsync([NetworkToolIds.Pip]);

        Assert.True(loaded.Success);
        ProviderSourceNetworkSelection selection = Assert.Single(loaded.Selections);
        Assert.Equal(NetworkToolIds.Pip, selection.NetworkToolId);
        NetworkSettingsResolutionResult nuget = NetworkSettingsResolver.Resolve(
            loaded.Settings!,
            NetworkToolIds.NuGet);
        Assert.True(nuget.Success);
        Assert.Null(nuget.EffectiveSettings!.Mirror);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
