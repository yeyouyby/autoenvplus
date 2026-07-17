using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Networking;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProviderSourceNetworkProjectionTests
{
    [Fact]
    public void Project_MapsAllKnownCatalogSourcesAndPreservesProxyOwnership()
    {
        NetworkSettings network = new(
            new GlobalNetworkSettings(
                HttpProxy: "http://proxy.example:8080",
                HttpsProxy: "https://secure-proxy.example:8443",
                NoProxy: ["localhost"]),
            new Dictionary<string, ToolNetworkSettings>
            {
                [NetworkToolIds.Pip] = new(
                    HttpProxy: NetworkEndpointOverride.Disabled,
                    Mirror: NetworkEndpointOverride.Custom("https://legacy.example/simple")),
            });

        ProviderSourceNetworkProjectionResult result = ProviderSourceNetworkProjection.Project(
            BuiltInLanguageCatalog.Current,
            ProviderSourcePreferences.Empty,
            network);

        Assert.True(result.Success);
        Assert.Equal(7, result.Selections.Count);
        EffectiveNetworkSettings pip = Resolve(result.Settings!, NetworkToolIds.Pip);
        Assert.Null(pip.HttpProxy);
        Assert.Equal("https://secure-proxy.example:8443/", pip.HttpsProxy!.AbsoluteUri);
        Assert.Equal(["localhost"], pip.NoProxy);
        Assert.Equal("https://pypi.org/simple", pip.Mirror!.AbsoluteUri);
        Assert.Equal(
            ProviderSourceOrigin.CatalogDefault,
            Assert.Single(result.Selections, item => item.NetworkToolId == NetworkToolIds.Pip).Origin);
        Assert.Equal(
            "https://registry.npmjs.org/",
            Resolve(result.Settings!, NetworkToolIds.Npm).Mirror!.AbsoluteUri);
        Assert.Equal(
            "https://api.nuget.org/v3/index.json",
            Resolve(result.Settings!, NetworkToolIds.NuGet).Mirror!.AbsoluteUri);
        Assert.Equal(
            "https://repo1.maven.org/maven2",
            Resolve(result.Settings!, NetworkToolIds.Maven).Mirror!.AbsoluteUri);
        Assert.Equal(
            "https://services.gradle.org/distributions",
            Resolve(result.Settings!, NetworkToolIds.Gradle).Mirror!.AbsoluteUri);
    }

    [Fact]
    public void Project_UsesOverrideBeforeDefaultAndSingleEnabledCustomBeforeOverride()
    {
        ProviderSourceOwner catalogOwner = new(
            "pip",
            "bundled",
            "python-package-index");
        ProviderSourceOwner customOwner = catalogOwner with { SlotId = "company-pypi" };
        ProviderSourcePreferences overridden = new(
            [new ProviderMirrorSlotOverride(
                catalogOwner,
                new Uri("https://override.example/simple"))],
            []);

        ProviderSourceNetworkProjectionResult overrideResult = ProviderSourceNetworkProjection.Project(
            BuiltInLanguageCatalog.Current,
            overridden,
            NetworkSettings.Default);
        Assert.True(overrideResult.Success);
        Assert.Equal(
            "https://override.example/simple",
            Resolve(overrideResult.Settings!, NetworkToolIds.Pip).Mirror!.AbsoluteUri);
        Assert.Equal(
            ProviderSourceOrigin.UserOverride,
            Assert.Single(
                overrideResult.Selections,
                item => item.NetworkToolId == NetworkToolIds.Pip).Origin);

        ProviderSourcePreferences custom = overridden with
        {
            CustomSources =
            [
                new CustomProviderSource(
                    customOwner,
                    "Company PyPI",
                    new Uri("https://company.example/simple"),
                    ProviderMirrorEndpointKind.PyPi,
                    "Company packages",
                    true),
            ],
        };
        ProviderSourceNetworkProjectionResult customResult = ProviderSourceNetworkProjection.Project(
            BuiltInLanguageCatalog.Current,
            custom,
            NetworkSettings.Default);

        Assert.True(customResult.Success);
        Assert.Equal(
            "https://company.example/simple",
            Resolve(customResult.Settings!, NetworkToolIds.Pip).Mirror!.AbsoluteUri);
        ProviderSourceNetworkSelection selected = Assert.Single(
            customResult.Selections,
            item => item.NetworkToolId == NetworkToolIds.Pip);
        Assert.Equal(ProviderSourceOrigin.Custom, selected.Origin);
        Assert.Equal(customOwner, selected.SourceOwner);
    }

    [Fact]
    public void Project_FailsClosedWhenMultipleSameKindCustomSourcesAreEnabled()
    {
        ProviderSourcePreferences preferences = new(
            [],
            [
                Custom("first-source", "https://first.example/simple", enabled: true),
                Custom("second-source", "https://second.example/simple", enabled: true),
            ]);

        ProviderSourceNetworkProjectionResult result = ProviderSourceNetworkProjection.Project(
            BuiltInLanguageCatalog.Current,
            preferences,
            NetworkSettings.Default);

        Assert.False(result.Success);
        Assert.Null(result.Settings);
        ProviderSourceNetworkProjectionError error = Assert.Single(
            result.Errors,
            item => item.Code
                == ProviderSourceNetworkProjectionErrorCode.MultipleEnabledCustomSources);
        Assert.Equal(NetworkToolIds.Pip, error.NetworkToolId);
        Assert.Contains("Enable only one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_IgnoresDisabledCustomSourceAndRejectsInvalidCatalogReference()
    {
        ProviderSourcePreferences disabled = new(
            [],
            [Custom("disabled-source", "https://disabled.example/simple", enabled: false)]);
        ProviderSourceNetworkProjectionResult disabledResult = ProviderSourceNetworkProjection.Project(
            BuiltInLanguageCatalog.Current,
            disabled,
            NetworkSettings.Default);
        Assert.True(disabledResult.Success);
        Assert.Equal(
            "https://pypi.org/simple",
            Resolve(disabledResult.Settings!, NetworkToolIds.Pip).Mirror!.AbsoluteUri);

        ProviderSourcePreferences invalid = new(
            [],
            [Custom("invalid-source", "https://invalid.example/simple", enabled: true) with
            {
                Owner = new ProviderSourceOwner("missing-tool", "bundled", "invalid-source"),
            }]);
        ProviderSourceNetworkProjectionResult invalidResult = ProviderSourceNetworkProjection.Project(
            BuiltInLanguageCatalog.Current,
            invalid,
            NetworkSettings.Default);
        Assert.False(invalidResult.Success);
        Assert.Contains(invalidResult.Errors, error =>
            error.Code == ProviderSourceNetworkProjectionErrorCode.InvalidSourcePreferences);
    }

    [Fact]
    public void ProjectForTools_IgnoresAmbiguityOwnedByAnUnrequestedTool()
    {
        ProviderSourcePreferences preferences = new(
            [],
            [
                NuGetCustom("first-nuget", "https://first.example/v3/index.json"),
                NuGetCustom("second-nuget", "https://second.example/v3/index.json"),
            ]);

        ProviderSourceNetworkProjectionResult pip =
            ProviderSourceNetworkProjection.ProjectForTools(
                BuiltInLanguageCatalog.Current,
                preferences,
                NetworkSettings.Default,
                [NetworkToolIds.Pip]);

        Assert.True(pip.Success);
        ProviderSourceNetworkSelection selection = Assert.Single(pip.Selections);
        Assert.Equal(NetworkToolIds.Pip, selection.NetworkToolId);
        Assert.Equal(
            "https://pypi.org/simple",
            Resolve(pip.Settings!, NetworkToolIds.Pip).Mirror!.AbsoluteUri);

        ProviderSourceNetworkProjectionResult nuget =
            ProviderSourceNetworkProjection.ProjectForTools(
                BuiltInLanguageCatalog.Current,
                preferences,
                NetworkSettings.Default,
                [NetworkToolIds.NuGet]);
        Assert.False(nuget.Success);
        Assert.Contains(nuget.Errors, error =>
            error.Code == ProviderSourceNetworkProjectionErrorCode.MultipleEnabledCustomSources);
    }

    [Fact]
    public void ProjectForTools_StillRejectsInvalidPreferenceStructure()
    {
        ProviderSourceNetworkProjectionResult result =
            ProviderSourceNetworkProjection.ProjectForTools(
                BuiltInLanguageCatalog.Current,
                new ProviderSourcePreferences(null!, []),
                NetworkSettings.Default,
                [NetworkToolIds.Pip]);

        Assert.False(result.Success);
        ProviderSourceNetworkProjectionError error = Assert.Single(result.Errors);
        Assert.Equal(
            ProviderSourceNetworkProjectionErrorCode.InvalidSourcePreferences,
            error.Code);
    }

    private static EffectiveNetworkSettings Resolve(NetworkSettings settings, string toolId)
    {
        NetworkSettingsResolutionResult result = NetworkSettingsResolver.Resolve(settings, toolId);
        Assert.True(result.Success);
        return result.EffectiveSettings!;
    }

    private static CustomProviderSource Custom(string slotId, string endpoint, bool enabled) => new(
        new ProviderSourceOwner("pip", "bundled", slotId),
        slotId,
        new Uri(endpoint),
        ProviderMirrorEndpointKind.PyPi,
        "Python packages",
        enabled);

    private static CustomProviderSource NuGetCustom(string slotId, string endpoint) => new(
        new ProviderSourceOwner("nuget-cli", "official-distribution", slotId),
        slotId,
        new Uri(endpoint),
        ProviderMirrorEndpointKind.NuGet,
        ".NET packages",
        true);
}
