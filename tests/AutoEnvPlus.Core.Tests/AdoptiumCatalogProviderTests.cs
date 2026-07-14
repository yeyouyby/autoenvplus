using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Providers.Java;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class AdoptiumCatalogProviderTests : IDisposable
{
    private const string ResponseJson = """
        [
          {
            "release_name": "jdk-21.0.11+10",
            "timestamp": "2026-04-23T06:32:33Z",
            "vendor": "eclipse",
            "version_data": {
              "major": 21,
              "minor": 0,
              "security": 11,
              "build": 10,
              "openjdk_version": "21.0.11+10-LTS",
              "optional": "LTS"
            },
            "binaries": [
              {
                "architecture": "x64",
                "image_type": "jdk",
                "os": "windows",
                "package": {
                  "name": "OpenJDK21U-jdk_x64_windows_hotspot_21.0.11_10.zip",
                  "link": "https://example.test/temurin21.zip",
                  "checksum": "d3625e7cadf23787ea540229544b6e2ab494b3b54da1801879e583e1dfee0a64",
                  "checksum_link": "https://example.test/temurin21.zip.sha256.txt",
                  "signature_link": "https://example.test/temurin21.zip.sig"
                }
              }
            ]
          }
        ]
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Java-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetReleasesAsync_ParsesTemurinJdkAndCachesItsAsset()
    {
        int requests = 0;
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
        {
            requests++;
            return StubHttpMessageHandler.Text(ResponseJson, "application/json");
        }));
        AdoptiumCatalogProvider provider = new(
            client,
            21,
            RuntimeArchitecture.X64,
            new Uri("https://api.example.test/v3/"));

        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        Assert.Equal(RuntimeVersion.Parse("21.0.11+10-LTS"), release.Version);
        Assert.Equal(RuntimeArchitecture.X64, release.Architecture);
        Assert.Contains("lts", release.Channels);
        Assert.Contains("latest", release.Channels);
        Assert.Equal(new DateOnly(2026, 4, 23), release.ReleaseDate);
        Assert.Equal("jdk-21.0.11+10", asset.ArchiveRootDirectory);
        Assert.Equal(new Uri("https://example.test/temurin21.zip"), asset.DownloadUri);
        PackageVerification verification = Assert.Single(asset.Verifications);
        Assert.Equal(PackageVerificationKind.ProviderChecksum, verification.Kind);
        Assert.Equal(new Uri("https://example.test/temurin21.zip.sha256.txt"), verification.SourceUri);
        Assert.Equal("OpenJDK21U-jdk_x64_windows_hotspot_21.0.11_10.zip", verification.Subject);
        Assert.Equal("SHA-256", verification.Algorithm);
        Assert.Equal("d3625e7cadf23787ea540229544b6e2ab494b3b54da1801879e583e1dfee0a64", verification.Value);
        Assert.Equal(PackageAuthenticityRequirement.DetachedPackageSignature, asset.AuthenticityRequirement);
        PackageSignatureRequirement signature = Assert.IsType<PackageSignatureRequirement>(
            asset.SignatureRequirement);
        Assert.Equal(PackageSignatureVerificationKind.OpenPgpDetached, signature.Kind);
        Assert.Equal(new Uri("https://example.test/temurin21.zip.sig"), signature.SignatureUri);
        Assert.Equal(AdoptiumCatalogProvider.SigningKeyFingerprint, signature.ExpectedPrimaryKeyFingerprint);
        Assert.Equal(asset.FileName, signature.SignedSubject);
        Assert.Empty(asset.SignatureVerifications);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CreateInstallPlan_UsesVendorVersionArchitectureAndJavaExecutable()
    {
        using HttpClient client = new(new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text(ResponseJson, "application/json")));
        AdoptiumCatalogProvider provider = new(
            client,
            21,
            RuntimeArchitecture.X64,
            new Uri("https://api.example.test/v3/"));
        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        ArchiveInstallPlan plan = provider.CreateInstallPlan(asset, _root);

        Assert.Equal(Path.Combine("bin", "java.exe"), plan.ExpectedExecutableRelativePath);
        Assert.EndsWith(
            Path.Combine("runtimes", "java", "temurin", "21.0.11+10-LTS", "x64"),
            plan.DestinationRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetReleasesAsync_IgnoresNonHttpsPackageLinks()
    {
        string unsafeResponse = ResponseJson.Replace(
            "https://example.test/temurin21.zip",
            "http://example.test/temurin21.zip",
            StringComparison.Ordinal);
        using HttpClient client = new(new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text(unsafeResponse, "application/json")));
        AdoptiumCatalogProvider provider = new(
            client,
            21,
            RuntimeArchitecture.X64,
            new Uri("https://api.example.test/v3/"));

        Assert.Empty(await provider.GetReleasesAsync());
    }

    [Fact]
    public async Task GetReleasesAsync_IgnoresAssetsWithoutDetachedSignature()
    {
        string unsignedResponse = ResponseJson.Replace(
            "\"signature_link\": \"https://example.test/temurin21.zip.sig\"",
            "\"unsigned\": true",
            StringComparison.Ordinal);
        using HttpClient client = new(new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text(unsignedResponse, "application/json")));
        AdoptiumCatalogProvider provider = new(
            client,
            21,
            RuntimeArchitecture.X64,
            new Uri("https://api.example.test/v3/"));

        Assert.Empty(await provider.GetReleasesAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
