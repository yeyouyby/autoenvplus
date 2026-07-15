using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Providers.NodeJs;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class NodeJsCatalogProviderTests
{
    private const string IndexJson = """
        [
          {
            "version": "v24.1.0",
            "date": "2026-01-10",
            "files": ["win-x64-zip", "win-arm64-zip"],
            "lts": false,
            "security": false
          },
          {
            "version": "v22.17.0",
            "date": "2025-06-24",
            "files": ["win-x64-zip", "win-x86-zip"],
            "lts": "Jod",
            "security": true
          },
          {
            "version": "v23.0.0-rc.1",
            "date": "2025-01-01",
            "files": ["win-x64-zip"],
            "lts": false,
            "security": false
          }
        ]
        """;

    [Fact]
    public async Task GetReleasesAsync_ParsesArchitecturesChannelsAndSecurity()
    {
        using HttpClient client = new(new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text(IndexJson, "application/json")));
        NodeJsCatalogProvider provider = new(
            client,
            new Uri("https://example.test/dist/"),
            new StubSignatureVerifier(string.Empty));

        IReadOnlyList<RuntimeRelease> releases = await provider.GetReleasesAsync();

        Assert.Equal(4, releases.Count);
        RuntimeRelease currentX64 = Assert.Single(releases,
            item => item.Version == RuntimeVersion.Parse("24.1.0")
                && item.Architecture == RuntimeArchitecture.X64);
        Assert.Contains("current", currentX64.Channels);
        Assert.Contains("latest", currentX64.Channels);

        RuntimeRelease ltsX64 = Assert.Single(releases,
            item => item.Version == RuntimeVersion.Parse("22.17.0")
                && item.Architecture == RuntimeArchitecture.X64);
        Assert.Contains("lts", ltsX64.Channels);
        Assert.Contains("jod", ltsX64.Channels);
        Assert.True(ltsX64.IsSecurityRelease);
        Assert.Equal(new DateOnly(2025, 6, 24), ltsX64.ReleaseDate);
    }

    [Fact]
    public async Task GetAssetAsync_BindsExactFileToOfficialChecksum()
    {
        const string expectedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using HttpClient client = new(new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text(IndexJson, "application/json")));
        StubSignatureVerifier signatureVerifier = new(
            $"{new string('b', 64)}  unrelated.zip\n{expectedHash}  node-v22.17.0-win-x64.zip\n");
        NodeJsCatalogProvider provider = new(
            client,
            new Uri("https://example.test/dist/"),
            signatureVerifier);
        RuntimeRelease release = (await provider.GetReleasesAsync()).Single(
            item => item.Version == RuntimeVersion.Parse("22.17.0")
                && item.Architecture == RuntimeArchitecture.X64);

        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        Assert.Equal(expectedHash, asset.PackageHash);
        Assert.Equal("node-v22.17.0-win-x64.zip", asset.FileName);
        Assert.Equal(
            new Uri("https://example.test/dist/v22.17.0/node-v22.17.0-win-x64.zip"),
            asset.DownloadUri);
        Assert.Equal("node-v22.17.0-win-x64", asset.ArchiveRootDirectory);
        Assert.Equal(PackageAuthenticityRequirement.SignedChecksumManifest, asset.AuthenticityRequirement);
        PackageVerification verification = Assert.Single(asset.Verifications);
        Assert.Equal(PackageVerificationKind.ProviderChecksum, verification.Kind);
        Assert.Equal(new Uri("https://example.test/dist/v22.17.0/SHASUMS256.txt.asc"), verification.SourceUri);
        Assert.Equal("node-v22.17.0-win-x64.zip", verification.Subject);
        Assert.Equal("SHA-256", verification.Algorithm);
        Assert.Equal(expectedHash, verification.Value);
        PackageSignatureVerification signature = Assert.Single(asset.SignatureVerifications);
        Assert.Equal(PackageSignatureVerificationKind.OpenPgpCleartext, signature.Kind);
        Assert.Equal(new Uri("https://example.test/dist/v22.17.0/SHASUMS256.txt.asc"), signature.SignatureUri);
        Assert.Equal(signatureVerifier.RequestedUri, signature.SignatureUri);
    }

    [Fact]
    public async Task GetAssetAsync_RejectsMissingChecksum()
    {
        using HttpClient client = new(new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text(IndexJson, "application/json")));
        NodeJsCatalogProvider provider = new(
            client,
            new Uri("https://example.test/dist/"),
            new StubSignatureVerifier($"{new string('a', 64)}  other.zip\n"));
        RuntimeRelease release = (await provider.GetReleasesAsync()).Single(
            item => item.Version == RuntimeVersion.Parse("22.17.0")
                && item.Architecture == RuntimeArchitecture.X64);

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetAssetAsync(release));
    }

    private sealed class StubSignatureVerifier(string content) : INodeReleaseSignatureVerifier
    {
        public Uri? RequestedUri { get; private set; }

        public Task<VerifiedNodeReleaseChecksums> GetVerifiedChecksumsAsync(
            Uri signedChecksumsUri,
            DateOnly? releaseDate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedUri = signedChecksumsUri;
            return Task.FromResult(new VerifiedNodeReleaseChecksums(
                content,
                new PackageSignatureVerification(
                    PackageSignatureVerificationKind.OpenPgpCleartext,
                    signedChecksumsUri,
                    new Uri("https://keys.example.test/release-key.asc"),
                    "SHASUMS256.txt",
                    "SHA-256",
                    "C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C",
                    "C43CEC45C17AB93C",
                    new DateTimeOffset(2025, 6, 24, 12, 0, 0, TimeSpan.Zero),
                    PackageSignerTrust.ActiveAtTrustSnapshot)));
        }
    }
}
