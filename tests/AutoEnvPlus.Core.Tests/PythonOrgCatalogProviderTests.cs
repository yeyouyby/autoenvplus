using System.Security.Cryptography;
using System.Text;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Providers.Python;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class PythonOrgCatalogProviderTests : IDisposable
{
    private const string ReleaseIndex = """
        [
          {
            "name": "Python 3.14.6",
            "is_published": true,
            "is_latest": true,
            "release_date": "2026-06-10T13:13:18Z",
            "pre_release": false,
            "resource_uri": "https://www.python.org/api/v2/downloads/release/1110/"
          },
          {
            "name": "Python 3.15.0rc1",
            "is_published": true,
            "is_latest": false,
            "release_date": "2026-07-01T00:00:00Z",
            "pre_release": true,
            "resource_uri": "https://www.python.org/api/v2/downloads/release/1120/"
          },
          {
            "name": "Python 2.7.18",
            "is_published": true,
            "is_latest": false,
            "release_date": "2020-04-20T00:00:00Z",
            "pre_release": false,
            "resource_uri": "https://www.python.org/api/v2/downloads/release/900/"
          }
        ]
        """;

    private const string PythonPackageHash = "75afa83f93b284d19040e24bc440ab741c09582c0d5310504d607a4e08c3dbaf";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Python-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetAssetAsync_VerifiesManifestAndSelectsStandardPythonCorePackage()
    {
        byte[] manifest = CreateManifest();
        string manifestHash = Sha256(manifest);
        int requests = 0;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            requests++;
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/release/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Text(ReleaseIndex, "application/json");
            }

            if (path.EndsWith("/release_file/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Text(CreateReleaseFiles(manifestHash), "application/json");
            }

            return StubHttpMessageHandler.Bytes(manifest);
        }));
        PythonOrgCatalogProvider provider = new(
            client,
            RuntimeArchitecture.X64,
            new Uri("https://api.example.test/downloads/"),
            new StubPythonReleaseSignatureVerifier());

        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        Assert.Equal(RuntimeVersion.Parse("3.14.6"), release.Version);
        Assert.Contains("latest", release.Channels);
        Assert.Equal(new DateOnly(2026, 6, 10), release.ReleaseDate);
        Assert.Equal("python-3.14.6-amd64.zip", asset.FileName);
        Assert.Equal(PythonPackageHash, asset.PackageHash);
        Assert.Null(asset.ArchiveRootDirectory);
        Assert.Contains(asset.Verifications, verification =>
            verification.Kind == PackageVerificationKind.VerifiedManifest
            && verification.SourceUri == new Uri("https://files.example.test/windows-3.14.6.json")
            && verification.Subject == "Windows release manifest"
            && verification.Value == manifestHash);
        Assert.Contains(asset.Verifications, verification =>
            verification.Kind == PackageVerificationKind.ProviderChecksum
            && verification.SourceUri == new Uri("https://files.example.test/windows-3.14.6.json")
            && verification.Subject == "python-3.14.6-amd64.zip"
            && verification.Value == PythonPackageHash);
        PackageSignatureVerification signature = Assert.Single(asset.SignatureVerifications);
        Assert.Equal(PackageSignatureVerificationKind.SigstoreBundle, signature.Kind);
        Assert.Equal("hugo@python.org", signature.CertificateIdentity);
        Assert.Equal("https://github.com/login/oauth", signature.CertificateOidcIssuer);
        Assert.Equal(PackageAuthenticityRequirement.SignedChecksumManifest, asset.AuthenticityRequirement);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task GetAssetAsync_RejectsTamperedWindowsManifest()
    {
        byte[] manifest = CreateManifest();
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/release/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Text(ReleaseIndex, "application/json");
            }

            if (path.EndsWith("/release_file/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Text(CreateReleaseFiles(new string('0', 64)), "application/json");
            }

            return StubHttpMessageHandler.Bytes(manifest);
        }));
        PythonOrgCatalogProvider provider = new(
            client,
            RuntimeArchitecture.X64,
            new Uri("https://api.example.test/downloads/"),
            new StubPythonReleaseSignatureVerifier());
        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetAssetAsync(release));
        Assert.Contains("release manifest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAssetAsync_RejectsReleaseWithoutSigstoreBundle()
    {
        byte[] manifest = CreateManifest();
        string releaseFiles = $$"""
            [
              {
                "name": "Windows release manifest",
                "url": "https://files.example.test/windows-3.14.6.json",
                "sha256_sum": "{{Sha256(manifest)}}"
              }
            ]
            """;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/release/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Text(ReleaseIndex, "application/json");
            }

            return StubHttpMessageHandler.Text(releaseFiles, "application/json");
        }));
        PythonOrgCatalogProvider provider = new(
            client,
            RuntimeArchitecture.X64,
            new Uri("https://api.example.test/downloads/"),
            new StubPythonReleaseSignatureVerifier());
        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetAssetAsync(release));

        Assert.Contains("Sigstore-signed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateInstallPlan_UsesVersionArchitectureAndPythonExecutable()
    {
        byte[] manifest = CreateManifest();
        string manifestHash = Sha256(manifest);
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/release/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Text(ReleaseIndex, "application/json");
            }

            if (path.EndsWith("/release_file/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Text(CreateReleaseFiles(manifestHash), "application/json");
            }

            return StubHttpMessageHandler.Bytes(manifest);
        }));
        PythonOrgCatalogProvider provider = new(
            client,
            RuntimeArchitecture.X64,
            new Uri("https://api.example.test/downloads/"),
            new StubPythonReleaseSignatureVerifier());
        RuntimeRelease release = Assert.Single(await provider.GetReleasesAsync());
        RuntimePackageAsset asset = await provider.GetAssetAsync(release);

        ArchiveInstallPlan plan = provider.CreateInstallPlan(asset, _root);

        Assert.Equal("python.exe", plan.ExpectedExecutableRelativePath);
        Assert.EndsWith(
            Path.Combine("runtimes", "python", "3.14.6", "x64"),
            plan.DestinationRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateManifest() => Encoding.UTF8.GetBytes("""
        {
          "versions": [
            {
              "id": "pythonembed-3.14-64",
              "url": "https://files.example.test/python-embed.zip",
              "hash": { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
            },
            {
              "id": "pythoncore-3.14t-64",
              "url": "https://files.example.test/python-freethreaded.zip",
              "hash": { "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
            },
            {
              "id": "pythoncore-3.14-64",
              "url": "https://files.example.test/python-3.14.6-amd64.zip",
              "hash": { "sha256": "75afa83f93b284d19040e24bc440ab741c09582c0d5310504d607a4e08c3dbaf" }
            }
          ]
        }
        """);

    private static string CreateReleaseFiles(string manifestHash) => $$"""
        [
          {
            "name": "Windows installer (64-bit)",
            "url": "https://files.example.test/python.exe",
            "sha256_sum": "{{new string('c', 64)}}"
          },
          {
            "name": "Windows release manifest",
            "url": "https://files.example.test/windows-3.14.6.json",
            "sha256_sum": "{{manifestHash}}",
            "sigstore_bundle_file": "https://files.example.test/windows-3.14.6.json.sigstore"
          }
        ]
        """;

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class StubPythonReleaseSignatureVerifier : IPythonReleaseSignatureVerifier
    {
        public Task<PackageSignatureVerification> VerifyAsync(
            ReadOnlyMemory<byte> manifest,
            Uri manifestUri,
            Uri bundleUri,
            PythonReleaseSigningPolicy signingPolicy,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(manifest.IsEmpty);
            return Task.FromResult(new PackageSignatureVerification(
                PackageSignatureVerificationKind.SigstoreBundle,
                bundleUri,
                PythonReleaseSignatureVerifier.TrustRootSourceUri,
                Path.GetFileName(manifestUri.LocalPath),
                "SHA-256",
                new string('a', 64),
                new string('b', 40),
                new DateTimeOffset(2026, 6, 10, 13, 11, 40, TimeSpan.Zero),
                PackageSignerTrust.ActiveAtTrustSnapshot,
                manifestUri,
                signingPolicy.CertificateIdentity,
                signingPolicy.OidcIssuer,
                123,
                456,
                Convert.ToBase64String(new byte[32]),
                PythonReleaseSignatureVerifier.TrustRootSha256,
                signingPolicy.PolicySourceUri));
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
