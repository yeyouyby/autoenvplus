using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Providers.Python;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Toolchains;

namespace AutoEnvPlus.Core.Tests;

public sealed class LanguageCatalogTests
{
    private static readonly string[] TopTen =
    [
        "python", "javascript", "typescript", "java", "c", "cpp", "csharp", "go",
        "rust", "php",
    ];

    [Fact]
    public void EmbeddedCatalog_HasBroadUniqueInventoryAndExactTopTen()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;

        Assert.True(catalog.Languages.Count >= 40, $"languages={catalog.Languages.Count}");
        Assert.True(catalog.Tools.Count >= 100, $"tools={catalog.Tools.Count}");
        Assert.Equal(
            TopTen.Order(StringComparer.Ordinal),
            catalog.DefaultLanguages.Select(language => language.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            catalog.Languages.Count,
            catalog.Languages.Select(language => language.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            catalog.Tools.Count,
            catalog.Tools.Select(tool => tool.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(
            catalog.Languages,
            language => Assert.NotEmpty(catalog.GetToolsForLanguage(language.Id)));
        Assert.True(catalog.ProviderProfiles.Count >= 100);
        Assert.Equal(
            catalog.ProviderProfiles.Count,
            catalog.ProviderProfiles.Select(profile => profile.Identity.ScopedId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public void ProviderProfiles_AreToolScopedAndExposeActualAdapters()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;

        Assert.True(catalog.TryGetProviderProfile(
            "cpython",
            "official-archive",
            out LanguageToolProviderProfile? cpython));
        Assert.True(cpython!.Capabilities.ManagedInstall);
        Assert.True(cpython.Capabilities.VersionSwitch);
        Assert.Equal(ToolProviderAdapterKind.ManagedArchive, cpython.AdapterKind);
        Assert.Equal(PythonOrgCatalogProvider.ProviderName, cpython.ExecutionProviderId);

        Assert.True(catalog.TryGetProviderProfile(
            "pypy",
            "official-archive",
            out LanguageToolProviderProfile? pypy));
        Assert.True(pypy!.Capabilities.Discover);
        Assert.True(pypy.Capabilities.SourceConfiguration);
        Assert.False(pypy.Capabilities.ManagedInstall);
        Assert.Equal(ToolProviderAdapterKind.MetadataOnly, pypy.AdapterKind);

        foreach (string toolId in new[] { "gcc", "clang" })
        {
            LanguageToolProviderProfile profile = Assert.Single(
                catalog.GetProviderProfilesForTool(toolId),
                profile => profile.Capabilities.ManagedInstall
                    && profile.AdapterKind == ToolProviderAdapterKind.WinGet);
            Assert.True(profile.Capabilities.VersionSwitch);
            Assert.True(profile.Capabilities.ProjectPin);
            Assert.True(profile.Capabilities.SessionActivation);
        }
    }

    [Fact]
    public void ProviderProfiles_DoNotInventManagedAdapters()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;

        Assert.All(
            catalog.ProviderProfiles.Where(profile => profile.Capabilities.ManagedInstall),
            profile =>
            {
                Assert.NotEqual(ToolProviderAdapterKind.MetadataOnly, profile.AdapterKind);
                Assert.False(string.IsNullOrWhiteSpace(profile.ExecutionProviderId));
                Assert.True(catalog.TryGetTool(
                    profile.Identity.LanguageToolId,
                    out LanguageToolDefinition? tool));
                Assert.Contains(
                    tool!.Providers,
                    provider => provider.Id.Equals(
                        profile.Identity.ProviderId,
                        StringComparison.OrdinalIgnoreCase)
                        && provider.ManagedInstallSupported);
            });
    }

    [Theory]
    [InlineData("python", "cpython")]
    [InlineData("python", "pypy")]
    [InlineData("javascript", "nodejs")]
    [InlineData("javascript", "deno")]
    [InlineData("javascript", "bun")]
    [InlineData("c", "msvc-build-tools")]
    [InlineData("c", "gcc")]
    [InlineData("c", "clang")]
    [InlineData("cpp", "msvc-build-tools")]
    [InlineData("cpp", "gcc")]
    [InlineData("cpp", "clang")]
    public void ConcreteImplementations_AreToolsUnderLanguages(
        string languageId,
        string toolId)
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;

        Assert.True(catalog.TryGetLanguage(languageId, out _));
        Assert.True(catalog.TryGetTool(toolId, out LanguageToolDefinition? tool));
        Assert.Contains(languageId, tool!.LanguageIds, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            catalog.Languages,
            language => language.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ToolCapabilities_AreBackedByProviderFacts()
    {
        foreach (LanguageToolDefinition tool in BuiltInLanguageCatalog.Current.Tools)
        {
            Assert.Equal(
                tool.Providers.Any(provider => provider.ManagedInstallSupported),
                tool.Capabilities.Install);
            Assert.Equal(
                tool.Providers.Any(provider => provider.MirrorSlots.Count > 0),
                tool.Capabilities.MirrorConfiguration);
            Assert.All(
                tool.Providers.Where(provider => provider.ManagedInstallSupported),
                provider => Assert.True(provider.DistributionKind is
                    LanguageToolProviderDistributionKind.ManagedArchive or
                    LanguageToolProviderDistributionKind.WinGet));
        }
    }

    [Fact]
    public void ManagedInstallClaims_AreLimitedToImplementedProviderFamilies()
    {
        string[] expected =
        [
            "clang", "cmake", "cpython", "dotnet-sdk", "eclipse-temurin", "gcc",
            "msvc-build-tools", "ninja", "nodejs",
        ];

        string[] actual = BuiltInLanguageCatalog.Current.Tools
            .Where(tool => tool.Capabilities.Install)
            .Select(tool => tool.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
        Assert.True(actual.Length < BuiltInLanguageCatalog.Current.Tools.Count / 10);
    }

    [Fact]
    public void Catalog_ContainsProviderOwnedMirrorSlots()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;

        AssertMirror(catalog, "cpython", ProviderMirrorEndpointKind.PyPi);
        AssertMirror(catalog, "nodejs", ProviderMirrorEndpointKind.Npm);
        AssertMirror(catalog, "dotnet-sdk", ProviderMirrorEndpointKind.NuGet);
        Assert.Contains(
            catalog.Tools.SelectMany(tool => tool.Providers)
                .SelectMany(provider => provider.MirrorSlots),
            slot => slot.EndpointKind == ProviderMirrorEndpointKind.Maven);
        Assert.All(
            catalog.Tools.SelectMany(tool => tool.Providers)
                .SelectMany(provider => provider.MirrorSlots),
            slot =>
            {
                Assert.Equal(Uri.UriSchemeHttps, slot.DefaultEndpoint.Scheme);
                Assert.False(string.IsNullOrWhiteSpace(slot.Purpose));
            });
    }

    [Theory]
    [InlineData(
        "cpython",
        "python-downloads",
        "https://www.python.org/api/v2/downloads/")]
    [InlineData("nodejs", "nodejs-downloads", "https://nodejs.org/dist/")]
    [InlineData("eclipse-temurin", "adoptium-downloads", "https://api.adoptium.net/v3/")]
    [InlineData(
        "dotnet-sdk",
        "dotnet-downloads",
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json")]
    public void ManagedProviderDownloadSlots_MatchProviderEndpointContracts(
        string toolId,
        string slotId,
        string expectedEndpoint)
    {
        Assert.True(BuiltInLanguageCatalog.Current.TryGetTool(
            toolId,
            out LanguageToolDefinition? tool));
        LanguageToolProviderDefinition provider = Assert.Single(
            tool!.Providers,
            candidate => candidate.ManagedInstallSupported);
        ProviderMirrorSlotDefinition slot = Assert.Single(
            provider.MirrorSlots,
            candidate => candidate.Id == slotId);

        Assert.Equal(ProviderMirrorEndpointKind.GenericDownload, slot.EndpointKind);
        Assert.Equal(expectedEndpoint, slot.DefaultEndpoint.AbsoluteUri);
        Assert.True(slot.UserOverridable);
    }

    [Fact]
    public void RuntimeBridge_CoversEveryRuntimeKindAndCatalogTool()
    {
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;

        Assert.Equal(Enum.GetValues<RuntimeKind>().Length, LanguageToolRuntimeBridge.Definitions.Count);
        foreach (RuntimeKind kind in Enum.GetValues<RuntimeKind>())
        {
            LanguageToolRuntimeBridgeDefinition bridge = LanguageToolRuntimeBridge.Get(kind);
            Assert.True(catalog.TryGetTool(bridge.ToolId, out LanguageToolDefinition? tool));
            Assert.All(
                bridge.LanguageIds,
                languageId => Assert.Contains(
                    languageId,
                    tool!.LanguageIds,
                    StringComparer.OrdinalIgnoreCase));
            LanguageToolRuntimeBinding plugin = LanguageToolRuntimeBridge.Resolve(
                kind,
                "plugin:example-provider");
            Assert.Equal(LanguageToolProviderBridgeKind.DeclarativePlugin, plugin.ProviderKind);
        }
    }

    [Fact]
    public void RuntimeBridge_MapsOfficialAndCppWinGetProviders()
    {
        LanguageToolRuntimeBinding python = LanguageToolRuntimeBridge.Resolve(
            RuntimeKind.Python,
            PythonOrgCatalogProvider.ProviderName);
        LanguageToolRuntimeBinding msvc = LanguageToolRuntimeBridge.Resolve(
            RuntimeKind.Msvc,
            LanguageToolRuntimeBridge.Get(ToolchainComponent.MsvcBuildTools).WinGetProviderId);

        Assert.Equal("cpython", python.ToolId);
        Assert.Equal(LanguageToolProviderBridgeKind.Official, python.ProviderKind);
        Assert.Equal("msvc-build-tools", msvc.ToolId);
        Assert.Equal(LanguageToolProviderBridgeKind.WinGet, msvc.ProviderKind);
        Assert.Throws<ArgumentException>(() => LanguageToolRuntimeBridge.Resolve(
            RuntimeKind.Python,
            "unknown-provider"));
    }

    [Fact]
    public void CppLanguages_ContainCompilerBuildAndWindowsSdkTools()
    {
        string[] required =
        [
            "msvc-build-tools", "gcc", "clang", "cmake", "ninja", "windows-sdk",
        ];
        LanguageCatalog catalog = BuiltInLanguageCatalog.Current;

        foreach (string languageId in new[] { "c", "cpp" })
        {
            string[] toolIds = catalog.GetToolsForLanguage(languageId)
                .Select(tool => tool.Id)
                .ToArray();
            Assert.All(required, toolId => Assert.Contains(toolId, toolIds));
        }

        Assert.True(catalog.TryGetTool(
            LanguageToolRuntimeBridge.GetToolId(new WindowsSdkInstallation(
                RuntimeVersion.Parse("10.0.26100.0"),
                @"C:\Program Files (x86)\Windows Kits\10",
                [RuntimeArchitecture.X64])),
            out LanguageToolDefinition? windowsSdk));
        Assert.Contains(LanguageToolRole.Sdk, windowsSdk!.Roles);
        Assert.True(windowsSdk.Capabilities.Discover);
        Assert.False(windowsSdk.Capabilities.Install);
    }

    private static void AssertMirror(
        LanguageCatalog catalog,
        string toolId,
        ProviderMirrorEndpointKind endpointKind)
    {
        Assert.True(catalog.TryGetTool(toolId, out LanguageToolDefinition? tool));
        Assert.Contains(
            tool!.Providers.SelectMany(provider => provider.MirrorSlots),
            slot => slot.EndpointKind == endpointKind && slot.UserOverridable);
    }
}
