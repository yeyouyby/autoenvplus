using System.Diagnostics;
using System.Text.Json;
using AutoEnvPlus.Cli;
using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class CliManagedInstallSourcePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-CliSources-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(
        RuntimeKind.Python,
        "cpython/official-archive/python-downloads",
        "https://www.python.org/api/v2/downloads/")]
    [InlineData(
        RuntimeKind.NodeJs,
        "nodejs/official-archive/nodejs-downloads",
        "https://nodejs.org/dist/")]
    [InlineData(
        RuntimeKind.Java,
        "eclipse-temurin/official-archive/adoptium-downloads",
        "https://api.adoptium.net/v3/")]
    [InlineData(
        RuntimeKind.DotNet,
        "dotnet-sdk/official-archive/dotnet-downloads",
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json")]
    public async Task ResolveAsync_UsesExactCatalogDownloadSlot(
        RuntimeKind runtimeKind,
        string expectedSourceId,
        string expectedEndpoint)
    {
        CliManagedInstallSourceResolution result =
            await CliManagedInstallSourceResolver.ResolveAsync(_root, runtimeKind);

        CliManagedInstallSourceSelection source = Assert.IsType<
            CliManagedInstallSourceSelection>(result.Selection);
        Assert.Null(result.Error);
        Assert.Equal(expectedSourceId, source.SourceId);
        Assert.Equal(expectedEndpoint, source.Endpoint.AbsoluteUri);
        Assert.Equal(ProviderSourceOrigin.CatalogDefault, source.Origin);
        Assert.Null(CliManagedInstallSourcePolicy.GetAcceptanceError(source, []));
    }

    [Fact]
    public async Task ResolveAsync_IgnoresLegacyRuntimeMirror()
    {
        NetworkSettingsSaveResult saved = await new NetworkSettingsStore(_root).SaveAsync(
            new NetworkSettings(
                new GlobalNetworkSettings(NoProxy: []),
                new Dictionary<string, ToolNetworkSettings>
                {
                    [NetworkToolIds.RuntimePython] = new(
                        Mirror: NetworkEndpointOverride.Custom(
                            "https://legacy-runtime-mirror.example/python/")),
                }));
        Assert.True(saved.Success);

        CliManagedInstallSourceSelection source = Assert.IsType<
            CliManagedInstallSourceSelection>((await CliManagedInstallSourceResolver.ResolveAsync(
                _root,
                RuntimeKind.Python)).Selection);

        Assert.Equal("https://www.python.org/api/v2/downloads/", source.Endpoint.AbsoluteUri);
        Assert.Equal(ProviderSourceOrigin.CatalogDefault, source.Origin);
    }

    [Fact]
    public async Task UserOverride_RequiresExplicitAcceptanceAndReportsIdentity()
    {
        ProviderSourceOwner owner = new(
            "dotnet-sdk",
            "official-archive",
            "dotnet-downloads");
        LanguageCatalog catalog = await new LanguagePackStore(_root).GetAvailableCatalogAsync();
        await new ProviderSourcePreferenceStore(_root).SetBuiltInOverrideAsync(
            catalog,
            owner,
            "https://dotnet-mirror.example/releases-index.json");

        CliManagedInstallSourceSelection source = Assert.IsType<
            CliManagedInstallSourceSelection>((await CliManagedInstallSourceResolver.ResolveAsync(
                _root,
                RuntimeKind.DotNet)).Selection);
        string error = Assert.IsType<string>(
            CliManagedInstallSourcePolicy.GetAcceptanceError(source, ["--yes"]));

        Assert.Equal(ProviderSourceOrigin.UserOverride, source.Origin);
        Assert.Contains(source.SourceId, error, StringComparison.Ordinal);
        Assert.Contains(source.Endpoint.AbsoluteUri, error, StringComparison.Ordinal);
        Assert.Contains(
            CliManagedInstallSourcePolicy.AcceptanceOption,
            error,
            StringComparison.Ordinal);
        Assert.Null(CliManagedInstallSourcePolicy.GetAcceptanceError(
            source,
            ["--ACCEPT-NON-DEFAULT-SOURCE"]));
    }

    [Fact]
    public async Task EnabledCustomDownloadSource_TakesPrecedenceAndRequiresAcceptance()
    {
        LanguageCatalog catalog = await new LanguagePackStore(_root).GetAvailableCatalogAsync();
        await new ProviderSourcePreferenceStore(_root).AddCustomSourceAsync(
            catalog,
            new ProviderSourceOwner("nodejs", "official-archive", "company-node-downloads"),
            "Company Node.js cache",
            "https://node-cache.example/dist/",
            ProviderMirrorEndpointKind.GenericDownload,
            "Company-managed Node.js distributions");

        CliManagedInstallSourceSelection source = Assert.IsType<
            CliManagedInstallSourceSelection>((await CliManagedInstallSourceResolver.ResolveAsync(
                _root,
                RuntimeKind.NodeJs)).Selection);

        Assert.Equal(ProviderSourceOrigin.Custom, source.Origin);
        Assert.Equal(
            "nodejs/official-archive/company-node-downloads",
            source.SourceId);
        Assert.Equal("https://node-cache.example/dist/", source.Endpoint.AbsoluteUri);
        Assert.NotNull(CliManagedInstallSourcePolicy.GetAcceptanceError(source, []));
    }

    [Fact]
    public async Task TwoEnabledCustomDownloadSources_AreRejectedAsAmbiguous()
    {
        ProviderSourcePreferenceStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreferencesPath)!);
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            overrides = Array.Empty<object>(),
            customSources = new[]
            {
                new
                {
                    languageToolId = "nodejs",
                    providerId = "official-archive",
                    slotId = "first-node-downloads",
                    displayName = "First Node.js cache",
                    endpoint = "https://first-node-cache.example/dist/",
                    endpointKind = "genericDownload",
                    purpose = "First Node.js distribution cache",
                    enabled = true,
                },
                new
                {
                    languageToolId = "nodejs",
                    providerId = "official-archive",
                    slotId = "second-node-downloads",
                    displayName = "Second Node.js cache",
                    endpoint = "https://second-node-cache.example/dist/",
                    endpointKind = "genericDownload",
                    purpose = "Second Node.js distribution cache",
                    enabled = true,
                },
            },
        });
        await File.WriteAllTextAsync(store.PreferencesPath, json);

        CliManagedInstallSourceResolution result =
            await CliManagedInstallSourceResolver.ResolveAsync(_root, RuntimeKind.NodeJs);

        Assert.Null(result.Selection);
        Assert.Contains(
            "More than one custom GenericDownload source is enabled",
            Assert.IsType<string>(result.Error),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledCustomDownloadSource_DoesNotReplaceExactCatalogSlot()
    {
        LanguageCatalog catalog = await new LanguagePackStore(_root).GetAvailableCatalogAsync();
        await new ProviderSourcePreferenceStore(_root).AddCustomSourceAsync(
            catalog,
            new ProviderSourceOwner("cpython", "official-archive", "disabled-python-cache"),
            "Disabled Python cache",
            "https://disabled-python-cache.example/downloads/",
            ProviderMirrorEndpointKind.GenericDownload,
            "Disabled Python distribution cache",
            enabled: false);

        CliManagedInstallSourceSelection source = Assert.IsType<
            CliManagedInstallSourceSelection>((await CliManagedInstallSourceResolver.ResolveAsync(
                _root,
                RuntimeKind.Python)).Selection);

        Assert.Equal("cpython/official-archive/python-downloads", source.SourceId);
        Assert.Equal("https://www.python.org/api/v2/downloads/", source.Endpoint.AbsoluteUri);
        Assert.Equal(ProviderSourceOrigin.CatalogDefault, source.Origin);
    }

    [Fact]
    public async Task InstallCommand_NonDefaultSourceFailsBeforeCatalogRequestWithoutAcceptance()
    {
        ProviderSourceOwner owner = new(
            "cpython",
            "official-archive",
            "python-downloads");
        const string endpoint = "https://unreachable.example/python/";
        const string legacyMirror = "https://legacy-runtime-mirror.example/python/";
        LanguageCatalog catalog = await new LanguagePackStore(_root).GetAvailableCatalogAsync();
        await new ProviderSourcePreferenceStore(_root).SetBuiltInOverrideAsync(
            catalog,
            owner,
            endpoint);
        NetworkSettingsSaveResult saved = await new NetworkSettingsStore(_root).SaveAsync(
            new NetworkSettings(
                new GlobalNetworkSettings(NoProxy: []),
                new Dictionary<string, ToolNetworkSettings>
                {
                    [NetworkToolIds.RuntimePython] = new(
                        Mirror: NetworkEndpointOverride.Custom(legacyMirror)),
                }));
        Assert.True(saved.Success);

        CliProcessResult result = await RunCliAsync(
            "install",
            "python",
            "3.13.5",
            "--root",
            _root,
            "--yes");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "cpython/official-archive/python-downloads",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains(endpoint, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(legacyMirror, result.StandardError, StringComparison.Ordinal);
        Assert.Contains(
            CliManagedInstallSourcePolicy.AcceptanceOption,
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "network request failed",
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CatalogCommand_NonDefaultSourceDoesNotRequireInstallAcceptance()
    {
        LanguageCatalog catalog = await new LanguagePackStore(_root).GetAvailableCatalogAsync();
        await new ProviderSourcePreferenceStore(_root).SetBuiltInOverrideAsync(
            catalog,
            new ProviderSourceOwner("cpython", "official-archive", "python-downloads"),
            "https://127.0.0.1:1/python/");

        CliProcessResult result = await RunCliAsync(
            "catalog",
            "python",
            "--root",
            _root,
            "--limit",
            "1");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "network request failed",
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            CliManagedInstallSourcePolicy.AcceptanceOption,
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DotNetSourceDeclaredHash_IsNeverPresentedAsVerifiedBy()
    {
        string label = CliInstallPlanPresentation.GetVerificationEvidenceLabel(
            RuntimeKind.DotNet,
            isThirdParty: false);

        Assert.Equal("Source-declared hash", label);
        Assert.DoesNotContain("Verified by", label, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "Verified by",
            CliInstallPlanPresentation.GetVerificationEvidenceLabel(
                RuntimeKind.Python,
                isThirdParty: false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task<CliProcessResult> RunCliAsync(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(CliManagedInstallSourceResolver).Assembly.Location);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The AutoEnvPlus CLI test process did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The AutoEnvPlus CLI test process did not exit before making a network request.");
        }

        return new CliProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private sealed record CliProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
