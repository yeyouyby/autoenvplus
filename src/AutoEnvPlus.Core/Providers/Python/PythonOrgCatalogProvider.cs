using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Providers.Python;

public sealed partial class PythonOrgCatalogProvider : IArchiveRuntimeProvider
{
    public const string ProviderName = "python-org";

    private static readonly Uri DefaultApiBaseUri = new("https://www.python.org/api/v2/downloads/");
    private readonly HttpClient _httpClient;
    private readonly Uri _apiBaseUri;
    private readonly RuntimeArchitecture _architecture;
    private readonly IPythonReleaseSignatureVerifier _signatureVerifier;
    private readonly ConcurrentDictionary<string, int> _releaseIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuntimePackageAsset> _assets = new(StringComparer.Ordinal);

    public PythonOrgCatalogProvider(
        HttpClient httpClient,
        RuntimeArchitecture architecture = RuntimeArchitecture.X64,
        Uri? apiBaseUri = null,
        IPythonReleaseSignatureVerifier? signatureVerifier = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _architecture = architecture is RuntimeArchitecture.X64 or RuntimeArchitecture.X86 or RuntimeArchitecture.Arm64
            ? architecture
            : throw new NotSupportedException($"python.org does not provide a Windows package for '{architecture}'.");
        _apiBaseUri = EnsureTrailingSlash(apiBaseUri ?? DefaultApiBaseUri);
        _signatureVerifier = signatureVerifier ?? new PythonReleaseSignatureVerifier(_httpClient);
    }

    public string Id => ProviderName;

    public RuntimeKind Kind => RuntimeKind.Python;

    public async Task<IReadOnlyList<RuntimeRelease>> GetReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            new Uri(_apiBaseUri, "release/?is_published=true"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The python.org release response is not a JSON array.");
        }

        List<RuntimeRelease> releases = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("pre_release", out JsonElement prerelease)
                && prerelease.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (!TryGetString(item, "name", out string? name))
            {
                continue;
            }

            Match nameMatch = StableReleaseNamePattern().Match(name);
            if (!nameMatch.Success
                || !RuntimeVersion.TryParse(nameMatch.Groups["version"].Value, out RuntimeVersion? version)
                || version!.Major < 3
                || !TryGetString(item, "resource_uri", out string? resourceUri)
                || !TryParseReleaseId(resourceUri, out int releaseId))
            {
                continue;
            }

            string providerVersion = version.ToString();
            _releaseIds[providerVersion] = releaseId;
            List<string> channels = ["stable"];
            if (item.TryGetProperty("is_latest", out JsonElement latest)
                && latest.ValueKind == JsonValueKind.True)
            {
                channels.Add("latest");
            }

            releases.Add(new RuntimeRelease(
                Id,
                providerVersion,
                Kind,
                version,
                _architecture,
                "Python Software Foundation",
                ParseDate(item),
                channels,
                false));
        }

        return releases
            .OrderByDescending(release => release.Version)
            .ToArray();
    }

    public async Task<RuntimePackageAsset> GetAssetAsync(
        RuntimeRelease release,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);

        if (_assets.TryGetValue(release.ProviderVersion, out RuntimePackageAsset? cached))
        {
            return cached;
        }

        if (!_releaseIds.TryGetValue(release.ProviderVersion, out int releaseId))
        {
            await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            if (!_releaseIds.TryGetValue(release.ProviderVersion, out releaseId))
            {
                throw new InvalidDataException(
                    $"python.org no longer contains release '{release.ProviderVersion}'.");
            }
        }

        VerifiedManifest manifest = await GetVerifiedWindowsManifestAsync(
            release,
            releaseId,
            cancellationToken).ConfigureAwait(false);
        RuntimePackageAsset asset = ParsePythonCoreAsset(release, manifest);
        _assets[release.ProviderVersion] = asset;
        return asset;
    }

    public ArchiveInstallPlan CreateInstallPlan(
        RuntimePackageAsset asset,
        string managedRoot)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ValidateRelease(asset.Release);

        string destination = Path.Combine(
            Path.GetFullPath(managedRoot),
            "runtimes",
            "python",
            asset.Release.Version.ToString(),
            asset.Release.Architecture.ToString().ToLowerInvariant());
        return new ArchiveInstallPlan(
            asset,
            Path.GetFullPath(managedRoot),
            destination,
            "python.exe");
    }

    private async Task<VerifiedManifest> GetVerifiedWindowsManifestAsync(
        RuntimeRelease release,
        int releaseId,
        CancellationToken cancellationToken)
    {
        Uri filesUri = new(_apiBaseUri, $"release_file/?release={releaseId}");
        using HttpResponseMessage response = await _httpClient.GetAsync(
            filesUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument filesDocument = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (filesDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The python.org release-file response is not a JSON array.");
        }

        Uri? manifestUri = null;
        Uri? sigstoreBundleUri = null;
        string? expectedHash = null;
        foreach (JsonElement file in filesDocument.RootElement.EnumerateArray())
        {
            if (TryGetString(file, "name", out string? name)
                && name.Equals("Windows release manifest", StringComparison.OrdinalIgnoreCase)
                && TryGetString(file, "url", out string? url)
                && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUri)
                && parsedUri.Scheme == Uri.UriSchemeHttps
                && TryGetString(file, "sha256_sum", out string? sha256)
                && IsSha256(sha256)
                && TryGetString(file, "sigstore_bundle_file", out string? sigstoreBundle)
                && Uri.TryCreate(sigstoreBundle, UriKind.Absolute, out Uri? parsedBundleUri)
                && parsedBundleUri.Scheme == Uri.UriSchemeHttps)
            {
                manifestUri = parsedUri;
                sigstoreBundleUri = parsedBundleUri;
                expectedHash = sha256;
                break;
            }
        }

        if (manifestUri is null || sigstoreBundleUri is null || expectedHash is null)
        {
            throw new InvalidDataException(
                "This Python release does not publish a Sigstore-signed Windows install manifest.");
        }

        byte[] content = await _httpClient.GetByteArrayAsync(manifestUri, cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch for the Python Windows release manifest. Expected {expectedHash}, got {actualHash}.");
        }

        PythonReleaseSigningPolicy signingPolicy = PythonReleaseSigningPolicy.ForVersion(release.Version);
        PackageSignatureVerification signature = await _signatureVerifier.VerifyAsync(
            content,
            manifestUri,
            sigstoreBundleUri,
            signingPolicy,
            cancellationToken).ConfigureAwait(false);
        return new VerifiedManifest(
            content,
            manifestUri,
            expectedHash.ToLowerInvariant(),
            signature);
    }

    private RuntimePackageAsset ParsePythonCoreAsset(RuntimeRelease release, VerifiedManifest verifiedManifest)
    {
        using JsonDocument manifest = JsonDocument.Parse(verifiedManifest.Content);
        if (!manifest.RootElement.TryGetProperty("versions", out JsonElement versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Python Windows release manifest has no versions array.");
        }

        string architectureSuffix = _architecture switch
        {
            RuntimeArchitecture.X64 => "64",
            RuntimeArchitecture.X86 => "32",
            RuntimeArchitecture.Arm64 => "arm64",
            _ => string.Empty,
        };
        Regex expectedId = new(
            $@"^pythoncore-\d+\.\d+-{Regex.Escape(architectureSuffix)}$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        foreach (JsonElement item in versions.EnumerateArray())
        {
            if (!TryGetString(item, "id", out string? id)
                || !expectedId.IsMatch(id)
                || !TryGetString(item, "url", out string? url)
                || !Uri.TryCreate(url, UriKind.Absolute, out Uri? downloadUri)
                || downloadUri.Scheme != Uri.UriSchemeHttps
                || !item.TryGetProperty("hash", out JsonElement hash)
                || !TryGetString(hash, "sha256", out string? sha256)
                || !IsSha256(sha256))
            {
                continue;
            }

            string fileName = Path.GetFileName(downloadUri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            return new RuntimePackageAsset(
                release,
                downloadUri,
                fileName,
                sha256.ToLowerInvariant(),
                RuntimePackageFormat.Zip,
                null,
                [
                    new PackageVerification(
                        PackageVerificationKind.VerifiedManifest,
                        verifiedManifest.Uri,
                        "Windows release manifest",
                        "SHA-256",
                        verifiedManifest.Sha256),
                    new PackageVerification(
                        PackageVerificationKind.ProviderChecksum,
                        verifiedManifest.Uri,
                        fileName,
                        "SHA-256",
                        sha256.ToLowerInvariant()),
                ],
                [verifiedManifest.Signature],
                PackageAuthenticityRequirement.SignedChecksumManifest);
        }

        throw new InvalidDataException(
            $"The Python Windows release manifest has no standard PythonCore package for {_architecture}.");
    }

    private void ValidateRelease(RuntimeRelease release)
    {
        if (!release.ProviderId.Equals(Id, StringComparison.Ordinal)
            || release.Kind != Kind
            || release.Architecture != _architecture)
        {
            throw new ArgumentException("The release does not belong to this python.org provider instance.", nameof(release));
        }
    }

    private static DateOnly? ParseDate(JsonElement item)
    {
        if (!TryGetString(item, "release_date", out string? releaseDate)
            || !DateTimeOffset.TryParse(
                releaseDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset value))
        {
            return null;
        }

        return DateOnly.FromDateTime(value.UtcDateTime);
    }

    private static bool TryParseReleaseId(string resourceUri, out int releaseId)
    {
        releaseId = 0;
        Match match = ReleaseIdPattern().Match(resourceUri);
        return match.Success
            && int.TryParse(
                match.Groups["id"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out releaseId);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool TryGetString(
        JsonElement item,
        string propertyName,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        return item.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()) is not null;
    }

    private static Uri EnsureTrailingSlash(Uri value)
    {
        if (!value.IsAbsoluteUri)
        {
            throw new ArgumentException("The provider API base URI must be absolute.", nameof(value));
        }

        return value.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? value
            : new Uri(value.AbsoluteUri + "/");
    }

    [GeneratedRegex(@"^Python\s+(?<version>\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StableReleaseNamePattern();

    [GeneratedRegex(@"/release/(?<id>\d+)/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseIdPattern();

    private sealed record VerifiedManifest(
        byte[] Content,
        Uri Uri,
        string Sha256,
        PackageSignatureVerification Signature);
}
