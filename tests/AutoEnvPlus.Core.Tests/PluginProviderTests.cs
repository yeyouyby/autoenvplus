using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Plugins;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class PluginProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-PluginProvider-{Guid.NewGuid():N}");

    [Fact]
    public async Task Provider_MapsAllArchitecturesToStaticCatalogAndThirdPartyEvidence()
    {
        RuntimeProviderPluginManifest manifest = CreateMultiArchitectureManifest();
        DeclarativeRuntimeCatalogProvider provider = new(manifest);

        IReadOnlyList<RuntimeRelease> releases = await provider.GetReleasesAsync();
        RuntimeRelease x64 = Assert.Single(
            releases,
            release => release.Architecture == RuntimeArchitecture.X64);
        RuntimeRelease arm64 = Assert.Single(
            releases,
            release => release.Architecture == RuntimeArchitecture.Arm64);
        RuntimePackageAsset x64Asset = await provider.GetAssetAsync(x64);
        RuntimePackageAsset arm64Asset = await provider.GetAssetAsync(arm64);

        Assert.Equal("plugin:community-python", provider.Id);
        Assert.Equal("community-python", provider.PluginId);
        Assert.Equal(RuntimeKind.Python, provider.Kind);
        Assert.All(releases, release => Assert.Equal(provider.Id, release.ProviderId));
        Assert.Equal(PackageAuthenticityRequirement.ChecksumEvidence, x64Asset.AuthenticityRequirement);
        Assert.Empty(x64Asset.SignatureVerifications);
        Assert.Null(x64Asset.SignatureRequirement);
        PackageVerification verification = Assert.Single(x64Asset.Verifications);
        Assert.Equal(PackageVerificationKind.ProviderChecksum, verification.Kind);
        Assert.Equal(
            "https://checksums.example/community-python-3.13.5/SHA256SUMS",
            verification.SourceUri.AbsoluteUri);
        Assert.Contains("Plugin-declared", verification.Subject, StringComparison.Ordinal);
        Assert.NotEqual(x64Asset.DownloadUri, verification.SourceUri);
        Assert.Equal(PackageHashAlgorithm.Sha256, x64Asset.HashAlgorithm);
        Assert.Equal(PackageHashAlgorithm.Sha512, arm64Asset.HashAlgorithm);
        Assert.Contains(
            "not treated as an official signature",
            DeclarativeRuntimeCatalogProvider.AuthenticityNotice,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_CreateInstallPlanAndRegistryIdIncludePluginIdentity()
    {
        DeclarativeRuntimeCatalogProvider provider = new(CreateMultiArchitectureManifest());
        RuntimeRelease release = (await provider.GetReleasesAsync()).Single(item =>
            item.Architecture == RuntimeArchitecture.X64);
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        var plan = provider.CreateInstallPlan(asset, _root);
        string runtimeId = provider.CreateManagedRuntimeId(release);

        Assert.Equal(Path.GetFullPath(_root), plan.ManagedRoot);
        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(_root),
                "runtimes",
                "python",
                "plugins",
                "community-python",
                "3.13.5",
                "x64"),
            plan.DestinationRoot);
        Assert.Equal("python.exe", plan.ExpectedExecutableRelativePath);
        Assert.Equal("python-3.13.5", plan.Asset.ArchiveRootDirectory);
        Assert.Equal("plugin-python-community-python-3.13.5-x64", runtimeId);
        Assert.Contains("community-python", plan.DestinationRoot, StringComparison.Ordinal);
        Assert.Contains("community-python", runtimeId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeKind.Msvc, "msvc", "bin/cl.exe")]
    [InlineData(RuntimeKind.Llvm, "llvm", "bin/clang.exe")]
    [InlineData(RuntimeKind.Mingw, "mingw", "bin/gcc.exe")]
    [InlineData(RuntimeKind.CMake, "cmake", "bin/cmake.exe")]
    [InlineData(RuntimeKind.Ninja, "ninja", "ninja.exe")]
    public async Task Provider_ToolchainKindsUseIsolatedPluginRuntimePaths(
        RuntimeKind expectedKind,
        string kindName,
        string expectedExecutable)
    {
        JsonObject document = PluginTestData.CreateManifestNode($"community-{kindName}");
        document["displayName"] = $"Community {kindName}";
        document["homepage"] = $"https://community.example/{kindName}";
        document["languageToolId"] = PluginTestData.LanguageToolIdForKindName(kindName);
        JsonObject assetNode = PluginTestData.FirstAsset(document);
        assetNode["fileName"] = $"community-{kindName}-3.13.5-win-x64.zip";
        assetNode["downloadUri"] =
            $"https://downloads.example/community-{kindName}-3.13.5-win-x64.zip";
        assetNode["checksumSourceUri"] =
            $"https://checksums.example/community-{kindName}-3.13.5/SHA256SUMS";
        assetNode["archiveRoot"] = $"{kindName}-3.13.5";
        assetNode["expectedExecutableRelativePath"] = expectedExecutable;
        DeclarativeRuntimeCatalogProvider provider = new(PluginTestData.Parse(document));
        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        var plan = provider.CreateInstallPlan(asset, _root);
        string runtimeId = provider.CreateManagedRuntimeId(release);

        Assert.Equal(expectedKind, provider.Kind);
        Assert.Equal($"plugin:community-{kindName}", provider.Id);
        Assert.Equal(expectedExecutable.Replace('/', '\\'), plan.ExpectedExecutableRelativePath);
        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(_root),
                "runtimes",
                kindName,
                "plugins",
                $"community-{kindName}",
                "3.13.5",
                "x64"),
            plan.DestinationRoot);
        Assert.Equal($"plugin-{kindName}-community-{kindName}-3.13.5-x64", runtimeId);
    }

    [Fact]
    public async Task Registry_RetainsInstalledRuntimeWhenPluginIdIsReusedForDifferentKind()
    {
        const string pluginId = "reusable-provider";
        RuntimeProviderPluginStore store = new(_root);
        ManagedRuntimeRegistry registry = new(_root);
        string sources = Path.Combine(_root, "sources");

        string pythonSource = await WriteManifestForKindAsync(
            sources,
            pluginId,
            "python",
            "python.exe");
        await store.ImportAsync(pythonSource);
        await store.EnableAsync(pluginId);
        DeclarativeRuntimeCatalogProvider pythonProvider = Assert.IsType<DeclarativeRuntimeCatalogProvider>(
            await new RuntimeProviderPluginRegistry(store).ResolveByIdAsync(pluginId));
        RuntimeRelease pythonRelease = Assert.Single(await pythonProvider.GetReleasesAsync());
        RuntimePackageAsset pythonAsset = await pythonProvider.GetAssetAsync(pythonRelease);
        ArchiveInstallPlan pythonPlan = pythonProvider.CreateInstallPlan(pythonAsset, _root);
        ManagedRuntimeEntry pythonEntry = CreateInstalledEntry(
            pythonProvider,
            pythonRelease,
            pythonPlan,
            pythonAsset);
        await registry.UpsertAsync(pythonEntry);

        await store.DeleteAsync(pluginId);
        string nodeSource = await WriteManifestForKindAsync(
            sources,
            pluginId,
            "nodejs",
            "node.exe");
        await store.ImportAsync(nodeSource);
        await store.EnableAsync(pluginId);
        DeclarativeRuntimeCatalogProvider nodeProvider = Assert.IsType<DeclarativeRuntimeCatalogProvider>(
            await new RuntimeProviderPluginRegistry(store).ResolveByIdAsync(pluginId));
        RuntimeRelease nodeRelease = Assert.Single(await nodeProvider.GetReleasesAsync());
        RuntimePackageAsset nodeAsset = await nodeProvider.GetAssetAsync(nodeRelease);
        ArchiveInstallPlan nodePlan = nodeProvider.CreateInstallPlan(nodeAsset, _root);
        ManagedRuntimeEntry nodeEntry = CreateInstalledEntry(
            nodeProvider,
            nodeRelease,
            nodePlan,
            nodeAsset);
        RegistryLoadResult registered = await registry.UpsertAsync(nodeEntry);

        Assert.NotEqual(pythonEntry.Id, nodeEntry.Id);
        Assert.Collection(
            registered.Entries.OrderBy(entry => entry.Kind),
            entry => Assert.Equal(RuntimeKind.Python, entry.Kind),
            entry => Assert.Equal(RuntimeKind.NodeJs, entry.Kind));
    }

    [Fact]
    public async Task Registry_RejectsEquivalentVersionWhenPluginIdIsReimportedWithNewMetadata()
    {
        const string pluginId = "reusable-provider";
        RuntimeProviderPluginStore store = new(_root);
        ManagedRuntimeRegistry registry = new(_root);
        string sources = Path.Combine(_root, "sources");

        string firstSource = await WriteManifestForKindAsync(
            sources,
            pluginId,
            "python",
            "python.exe",
            "3.13.5+first");
        await store.ImportAsync(firstSource);
        await store.EnableAsync(pluginId);
        ManagedRuntimeEntry firstEntry = await CreateInstalledEntryAsync(store, pluginId);
        await registry.UpsertAsync(firstEntry);

        await store.DeleteAsync(pluginId);
        string replacementSource = await WriteManifestForKindAsync(
            sources,
            pluginId,
            "python",
            "python.exe",
            "3.13.5+replacement");
        await store.ImportAsync(replacementSource);
        await store.EnableAsync(pluginId);
        ManagedRuntimeEntry replacementEntry = await CreateInstalledEntryAsync(store, pluginId);

        Assert.NotEqual(firstEntry.Id, replacementEntry.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.UpsertAsync(replacementEntry));
        ManagedRuntimeEntry retained = Assert.Single((await registry.LoadAsync()).Entries);
        Assert.Equal(firstEntry.Id, retained.Id);
        Assert.Equal(firstEntry.Version, retained.Version);
    }

    [Fact]
    public async Task Provider_RejectsForeignReleaseAndForeignAsset()
    {
        DeclarativeRuntimeCatalogProvider provider = new(CreateMultiArchitectureManifest());
        RuntimeRelease release = Assert.Single(
            await provider.GetReleasesAsync(),
            item => item.Architecture == RuntimeArchitecture.X64);
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);
        RuntimeRelease foreignRelease = release with { ProviderId = "python-org" };

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetAssetAsync(foreignRelease));
        Assert.Throws<ArgumentException>(() => provider.CreateManagedRuntimeId(foreignRelease));
        Assert.Throws<ArgumentException>(() => provider.CreateInstallPlan(
            asset with { FileName = "substituted.zip" },
            _root));
    }

    [Fact]
    public async Task Registry_ResolvesOnlyEnabledProviderByStableIdKindAndRelease()
    {
        string sourceDirectory = Path.Combine(_root, "sources");
        string first = await PluginTestData.WriteManifestAsync(
            sourceDirectory,
            "community-python");
        string second = await PluginTestData.WriteManifestAsync(
            sourceDirectory,
            "alternate-python",
            document =>
            {
                PluginTestData.FirstAsset(document)["downloadUri"] =
                    "https://alternate.example/python.zip";
                PluginTestData.FirstAsset(document)["checksumSourceUri"] =
                    "https://alternate.example/SHA256SUMS";
            });
        RuntimeProviderPluginStore store = new(_root);
        await store.ImportAsync(first);
        await store.ImportAsync(second);
        await store.EnableAsync("plugin:community-python");
        RuntimeProviderPluginRegistry registry = new(store);

        IReadOnlyList<DeclarativeRuntimeCatalogProvider> enabled =
            await registry.GetEnabledProvidersAsync();
        DeclarativeRuntimeCatalogProvider? byBareId = await registry.ResolveByIdAsync(
            "community-python");
        DeclarativeRuntimeCatalogProvider? byProviderId = await registry.ResolveByIdAsync(
            "plugin:community-python");
        IReadOnlyList<DeclarativeRuntimeCatalogProvider> byKind =
            await registry.ResolveByKindAsync(RuntimeKind.Python);
        RuntimeRelease? selected = await registry.ResolveReleaseAsync(
            "plugin:community-python",
            new RuntimeVersion(3, 13, 5),
            RuntimeArchitecture.X64);

        Assert.Single(enabled);
        Assert.Equal(enabled[0].Id, byBareId!.Id);
        Assert.NotSame(byBareId, byProviderId);
        Assert.Equal(byBareId.Id, byProviderId!.Id);
        Assert.Single(byKind);
        Assert.NotNull(selected);
        Assert.Equal("plugin:community-python", selected.ProviderId);
        Assert.Null(await registry.ResolveByIdAsync("alternate-python"));
        Assert.Null(await registry.ResolveReleaseAsync(
            "community-python",
            new RuntimeVersion(3, 13, 5),
            RuntimeArchitecture.Arm64));
    }

    [Fact]
    public async Task Registry_AllowsSameKindVersionArchitectureAcrossPluginProvidersWithoutCollision()
    {
        string sourceDirectory = Path.Combine(_root, "sources");
        string first = await PluginTestData.WriteManifestAsync(
            sourceDirectory,
            "community-python");
        string second = await PluginTestData.WriteManifestAsync(
            sourceDirectory,
            "alternate-python",
            document =>
            {
                PluginTestData.FirstAsset(document)["downloadUri"] =
                    "https://alternate.example/python.zip";
                PluginTestData.FirstAsset(document)["checksumSourceUri"] =
                    "https://alternate.example/SHA256SUMS";
            });
        RuntimeProviderPluginStore store = new(_root);
        await store.ImportAsync(first);
        await store.ImportAsync(second);
        await store.EnableAsync("community-python");
        await store.EnableAsync("alternate-python");

        IReadOnlyList<DeclarativeRuntimeCatalogProvider> providers =
            await new RuntimeProviderPluginRegistry(store).ResolveByKindAsync(RuntimeKind.Python);
        RuntimeRelease[] releases = await Task.WhenAll(providers.Select(async provider =>
            Assert.Single(await provider.GetReleasesAsync())));
        string[] runtimeIds = providers
            .Zip(releases, (provider, release) => provider.CreateManagedRuntimeId(release))
            .ToArray();
        string[] destinations = await Task.WhenAll(providers.Zip(
            releases,
            async (provider, release) => provider.CreateInstallPlan(
                await provider.GetAssetAsync(release),
                _root).DestinationRoot));

        Assert.Equal(2, providers.Count);
        Assert.Equal(2, releases.Select(release => release.ProviderId).Distinct().Count());
        Assert.Equal(2, runtimeIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void RuntimeProviderSelector_ConstrainsInstalledRuntimeToExactProvider()
    {
        RuntimeVersion version = new(3, 13, 5);
        ManagedRuntimeEntry official = CreateInstalledEntry(
            "python-official",
            "python-org",
            version);
        ManagedRuntimeEntry plugin = CreateInstalledEntry(
            "plugin-community-python-3.13.5-x64",
            "plugin:community-python",
            version);
        RuntimeProviderSelectionRequest request = new(
            "plugin:community-python",
            RuntimeKind.Python,
            VersionSelector.Parse("3.13"),
            RuntimeArchitecture.X64);

        RuntimeProviderSelectionResult result = RuntimeProviderSelector.ResolveInstalled(
            request,
            [official, plugin]);

        Assert.True(result.Success);
        Assert.Same(plugin, result.Entry);
        Assert.Equal("plugin:community-python", result.Entry!.ProviderId);
        Assert.Null(result.Error);
    }

    [Fact]
    public void RuntimeProviderSelector_DoesNotFallBackToDifferentProvider()
    {
        ManagedRuntimeEntry official = CreateInstalledEntry(
            "python-official",
            "python-org",
            new RuntimeVersion(3, 13, 5));
        RuntimeProviderSelectionRequest request = new(
            "plugin:community-python",
            RuntimeKind.Python,
            VersionSelector.Auto,
            RuntimeArchitecture.X64);

        RuntimeProviderSelectionResult result = RuntimeProviderSelector.ResolveInstalled(
            request,
            [official]);

        Assert.False(result.Success);
        Assert.Null(result.Entry);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Registry_RejectsAdditionalBuiltInProviderConflict()
    {
        string source = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"),
            "enterprise-python");
        RuntimeProviderPluginStore store = new(_root);
        await store.ImportAsync(source);
        await store.EnableAsync("enterprise-python");
        RuntimeProviderPluginRegistry registry = new(
            store,
            ["enterprise-python"]);

        RuntimeProviderPluginException exception = await Assert.ThrowsAsync<
            RuntimeProviderPluginException>(() => registry.GetEnabledProvidersAsync());

        Assert.Equal(RuntimeProviderPluginErrorCode.BuiltInProviderConflict, exception.Code);
    }

    [Fact]
    public async Task Registry_IgnoresCorruptDisabledPluginButFailsClosedForCorruptEnabledPlugin()
    {
        string disabledSource = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"),
            "disabled-python");
        string enabledSource = await PluginTestData.WriteManifestAsync(
            Path.Combine(_root, "sources"),
            "enabled-python",
            document =>
            {
                PluginTestData.FirstAsset(document)["downloadUri"] =
                    "https://enabled.example/python.zip";
                PluginTestData.FirstAsset(document)["checksumSourceUri"] =
                    "https://enabled.example/SHA256SUMS";
            });
        RuntimeProviderPluginStore store = new(_root);
        RuntimeProviderPluginDescriptor disabled = await store.ImportAsync(disabledSource);
        RuntimeProviderPluginDescriptor enabled = await store.ImportAsync(enabledSource);
        await store.EnableAsync(enabled.Id);
        await File.WriteAllTextAsync(disabled.ManifestPath, "{broken");
        RuntimeProviderPluginRegistry registry = new(store);

        IReadOnlyList<DeclarativeRuntimeCatalogProvider> providers =
            await registry.GetEnabledProvidersAsync();
        Assert.Single(providers);
        Assert.Equal("plugin:enabled-python", providers[0].Id);

        await File.WriteAllTextAsync(enabled.ManifestPath, "{broken");
        RuntimeProviderPluginException exception = await Assert.ThrowsAsync<
            RuntimeProviderPluginException>(() => registry.GetEnabledProvidersAsync());
        Assert.Equal(RuntimeProviderPluginErrorCode.MalformedJson, exception.Code);
    }

    [Fact]
    public async Task Provider_RejectsManagedRootReparsePoint()
    {
        string target = Path.Combine(_root, "real-root");
        string link = Path.Combine(_root, "linked-root");
        Directory.CreateDirectory(target);
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

        DeclarativeRuntimeCatalogProvider provider = new(CreateMultiArchitectureManifest());
        RuntimeRelease release = Assert.Single(
            await provider.GetReleasesAsync(),
            item => item.Architecture == RuntimeArchitecture.X64);
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        Assert.Throws<ArgumentException>(() => provider.CreateInstallPlan(asset, link));
    }

    [Fact]
    public async Task Provider_RejectsReparsePointInsideManagedDestinationPath()
    {
        string managedRoot = Path.Combine(_root, "managed");
        string external = Path.Combine(_root, "external-runtimes");
        Directory.CreateDirectory(managedRoot);
        Directory.CreateDirectory(external);
        string runtimesLink = Path.Combine(managedRoot, "runtimes");
        try
        {
            Directory.CreateSymbolicLink(runtimesLink, external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        DeclarativeRuntimeCatalogProvider provider = new(CreateMultiArchitectureManifest());
        RuntimeRelease release = Assert.Single(
            await provider.GetReleasesAsync(),
            item => item.Architecture == RuntimeArchitecture.X64);
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        Assert.Throws<ArgumentException>(() =>
            provider.CreateInstallPlan(asset, managedRoot));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(
                     _root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static RuntimeProviderPluginManifest CreateMultiArchitectureManifest()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        JsonArray assets = (JsonArray)document["releases"]![0]!["assets"]!;
        JsonObject arm64 = (JsonObject)assets[0]!.DeepClone();
        arm64["architecture"] = "arm64";
        arm64["fileName"] = "community-python-3.13.5-win-arm64.zip";
        arm64["downloadUri"] =
            "https://downloads.example/community-python-3.13.5-win-arm64.zip";
        arm64.Remove("sha256");
        arm64["sha512"] = PluginTestData.Sha512;
        assets.Add(arm64);
        return PluginTestData.Parse(document);
    }

    private ManagedRuntimeEntry CreateInstalledEntry(
        string id,
        string providerId,
        RuntimeVersion version) => new(
            id,
            providerId,
            RuntimeKind.Python,
            version,
            RuntimeArchitecture.X64,
            Path.Combine(_root, "installed", id),
            "python.exe",
            PluginTestData.Sha256,
            DateTimeOffset.UtcNow,
            ["stable"]);

    private static ManagedRuntimeEntry CreateInstalledEntry(
        DeclarativeRuntimeCatalogProvider provider,
        RuntimeRelease release,
        ArchiveInstallPlan plan,
        RuntimePackageAsset asset) => new(
            provider.CreateManagedRuntimeId(release),
            provider.Id,
            release.Kind,
            release.Version,
            release.Architecture,
            plan.DestinationRoot,
            plan.ExpectedExecutableRelativePath,
            asset.PackageHash,
            DateTimeOffset.UtcNow,
            release.Channels,
            asset.HashAlgorithm);

    private async Task<ManagedRuntimeEntry> CreateInstalledEntryAsync(
        RuntimeProviderPluginStore store,
        string pluginId)
    {
        DeclarativeRuntimeCatalogProvider provider = Assert.IsType<DeclarativeRuntimeCatalogProvider>(
            await new RuntimeProviderPluginRegistry(store).ResolveByIdAsync(pluginId));
        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);
        ArchiveInstallPlan plan = provider.CreateInstallPlan(asset, _root);
        return CreateInstalledEntry(provider, release, plan, asset);
    }

    private static Task<string> WriteManifestForKindAsync(
        string directory,
        string pluginId,
        string kindName,
        string executable,
        string version = "3.13.5") => PluginTestData.WriteManifestAsync(
            directory,
            pluginId,
            document =>
            {
                document["languageToolId"] = PluginTestData.LanguageToolIdForKindName(kindName);
                document["releases"]![0]!["version"] = version;
                JsonObject asset = PluginTestData.FirstAsset(document);
                asset["fileName"] = $"{pluginId}-{kindName}-{version}-win-x64.zip";
                asset["downloadUri"] =
                    $"https://downloads.example/{pluginId}-{kindName}-{version}-win-x64.zip";
                asset["checksumSourceUri"] =
                    $"https://checksums.example/{pluginId}-{kindName}-{version}/SHA256SUMS";
                asset["archiveRoot"] = $"{pluginId}-{kindName}-{version}";
                asset["expectedExecutableRelativePath"] = executable;
            });
}
