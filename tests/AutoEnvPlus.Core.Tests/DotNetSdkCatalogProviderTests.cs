using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Providers.DotNet;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class DotNetSdkCatalogProviderTests
{
    private const string IndexJson = """
        {
          "releases-index": [
            {
              "channel-version": "10.0",
              "latest-sdk": "10.0.302",
              "support-phase": "active",
              "release-type": "lts",
              "releases.json": "https://example.test/dotnet/10.0/releases.json"
            },
            {
              "channel-version": "11.0",
              "latest-sdk": "11.0.100-preview.1",
              "support-phase": "preview",
              "release-type": "sts",
              "releases.json": "https://example.test/dotnet/11.0/releases.json"
            }
          ]
        }
        """;

    private static readonly string SdkHash = new('a', 128);

    [Fact]
    public async Task GetReleasesAsync_ParsesStableSdkZipAndSha512Evidence()
    {
        using HttpClient client = CreateClient();
        DotNetSdkCatalogProvider provider = new(
            client,
            RuntimeArchitecture.X64,
            new Uri("https://example.test/dotnet/releases-index.json"));

        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        Assert.Equal(RuntimeKind.DotNet, release.Kind);
        Assert.Equal(RuntimeVersion.Parse("10.0.302"), release.Version);
        Assert.Equal(RuntimeArchitecture.X64, release.Architecture);
        Assert.Contains("lts", release.Channels);
        Assert.Contains("latest", release.Channels);
        Assert.True(release.IsSecurityRelease);
        Assert.Equal(new DateOnly(2026, 7, 14), release.ReleaseDate);
        Assert.Equal(PackageHashAlgorithm.Sha512, asset.HashAlgorithm);
        Assert.Equal(SdkHash, asset.PackageHash);
        Assert.Equal("dotnet-sdk-10.0.302-win-x64.zip", asset.FileName);
        Assert.Equal(PackageAuthenticityRequirement.ChecksumEvidence, asset.AuthenticityRequirement);
        Assert.Empty(asset.SignatureVerifications);
        PackageVerification verification = Assert.Single(asset.Verifications);
        Assert.Equal("SHA-512", verification.Algorithm);
        Assert.Equal(
            new Uri("https://example.test/dotnet/10.0/releases.json"),
            verification.SourceUri);
    }

    [Fact]
    public async Task GetReleasesAsync_IgnoresPreviewSdksAndOtherArchitectures()
    {
        using HttpClient client = CreateClient(includeStable: false);
        DotNetSdkCatalogProvider provider = new(
            client,
            RuntimeArchitecture.X64,
            new Uri("https://example.test/dotnet/releases-index.json"));

        IReadOnlyList<RuntimeRelease> releases = await provider.GetReleasesAsync();

        Assert.Empty(releases);
    }

    [Fact]
    public async Task GetReleasesAsync_SkipsStableSdkWithoutRequestedArchitecture()
    {
        using HttpClient client = CreateClient(includeX64: false);
        DotNetSdkCatalogProvider provider = new(
            client,
            RuntimeArchitecture.X64,
            new Uri("https://example.test/dotnet/releases-index.json"));

        IReadOnlyList<RuntimeRelease> releases = await provider.GetReleasesAsync();

        Assert.Empty(releases);
    }

    [Fact]
    public async Task CreateInstallPlan_UsesIsolatedDotNetSdkDirectory()
    {
        using HttpClient client = CreateClient();
        DotNetSdkCatalogProvider provider = new(
            client,
            RuntimeArchitecture.X64,
            new Uri("https://example.test/dotnet/releases-index.json"));
        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        AutoEnvPlus.Core.Installation.ArchiveInstallPlan plan = provider.CreateInstallPlan(
            asset,
            @"D:\managed");

        Assert.EndsWith(
            @"runtimes\dotnet\sdk\10.0.302\x64",
            plan.DestinationRoot,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("dotnet.exe", plan.ExpectedExecutableRelativePath);
        Assert.Null(asset.ArchiveRootDirectory);
    }

    private static HttpClient CreateClient(
        bool includeStable = true,
        bool includeX64 = true)
    {
        return new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                "releases-index.json",
                StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Text(IndexJson, "application/json");
            }

            string x64File = includeX64
                ? $$"""
                    {
                      "name": "dotnet-sdk-10.0.302-win-x64.zip",
                      "rid": "win-x64",
                      "url": "https://builds.example.test/dotnet-sdk-10.0.302-win-x64.zip",
                      "hash": "{{SdkHash}}"
                    },
                    """
                : string.Empty;
            string stableSdk = includeStable
                ? $$"""
                    {
                      "version": "10.0.302",
                      "files": [
                        null,
                        {{x64File}}
                        {
                          "name": "dotnet-sdk-10.0.302-win-arm64.zip",
                          "rid": "win-arm64",
                          "url": "https://builds.example.test/dotnet-sdk-10.0.302-win-arm64.zip",
                          "hash": "{{new string('b', 128)}}"
                        }
                      ]
                    }
                    """
                : """
                    {
                      "version": "10.0.400-preview.1",
                      "files": []
                    }
                    """;
            string releases = $$"""
                {
                  "releases": [
                    {
                      "release-date": "2026-07-14",
                      "security": true,
                      "sdks": [
                        {{stableSdk}}
                      ]
                    }
                  ]
                }
                """;
            return StubHttpMessageHandler.Text(releases, "application/json");
        }));
    }
}
