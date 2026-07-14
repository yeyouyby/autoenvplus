using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Providers.Java;

public sealed class AdoptiumCatalogProvider : IArchiveRuntimeProvider
{
    public const string ProviderName = "adoptium-temurin";
    public const string SigningKeyFingerprint = "3B04D753C9050D9A5D343F39843C48A565F8F04B";

    private static readonly Uri DefaultBaseUri = new("https://api.adoptium.net/v3/");
    private static readonly Uri SigningKeyUri = new(
        "https://keyserver.ubuntu.com/pks/lookup?op=get&search=0x3B04D753C9050D9A5D343F39843C48A565F8F04B");
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly int _featureVersion;
    private readonly RuntimeArchitecture _architecture;
    private readonly ConcurrentDictionary<string, RuntimePackageAsset> _assets = new(StringComparer.Ordinal);

    public AdoptiumCatalogProvider(
        HttpClient httpClient,
        int featureVersion,
        RuntimeArchitecture architecture = RuntimeArchitecture.X64,
        Uri? baseUri = null)
    {
        if (featureVersion < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(featureVersion), "Java feature versions before 8 are not supported.");
        }

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _featureVersion = featureVersion;
        _architecture = architecture is RuntimeArchitecture.X64 or RuntimeArchitecture.X86 or RuntimeArchitecture.Arm64
            ? architecture
            : throw new NotSupportedException($"Temurin does not support architecture '{architecture}' in AutoEnvPlus.");
        _baseUri = EnsureTrailingSlash(baseUri ?? DefaultBaseUri);
    }

    public string Id => ProviderName;

    public RuntimeKind Kind => RuntimeKind.Java;

    public async Task<IReadOnlyList<RuntimeRelease>> GetReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        string apiArchitecture = _architecture switch
        {
            RuntimeArchitecture.X64 => "x64",
            RuntimeArchitecture.X86 => "x32",
            RuntimeArchitecture.Arm64 => "aarch64",
            _ => throw new UnreachableException(),
        };
        string relativeUri = $"assets/feature_releases/{_featureVersion}/ga"
            + $"?architecture={apiArchitecture}"
            + "&heap_size=normal&image_type=jdk&jvm_impl=hotspot&os=windows"
            + "&page=0&page_size=20&project=jdk&sort_method=DATE&sort_order=DESC&vendor=eclipse";

        Uri requestUri = new(_baseUri, relativeUri);
        using HttpResponseMessage response = await _httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Adoptium release response is not a JSON array.");
        }

        List<RuntimeRelease> releases = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (!TryParseRelease(
                item,
                requestUri,
                releases.Count == 0,
                out RuntimeRelease? release,
                out RuntimePackageAsset? asset))
            {
                continue;
            }

            releases.Add(release!);
            _assets[release!.ProviderVersion] = asset!;
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

        if (_assets.TryGetValue(release.ProviderVersion, out RuntimePackageAsset? asset))
        {
            return asset;
        }

        await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
        return _assets.TryGetValue(release.ProviderVersion, out asset)
            ? asset
            : throw new InvalidDataException(
                $"The Temurin API no longer contains asset '{release.ProviderVersion}'.");
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
            "java",
            "temurin",
            asset.Release.Version.ToString(),
            asset.Release.Architecture.ToString().ToLowerInvariant());
        return new ArchiveInstallPlan(
            asset,
            Path.GetFullPath(managedRoot),
            destination,
            Path.Combine("bin", "java.exe"));
    }

    private bool TryParseRelease(
        JsonElement item,
        Uri requestUri,
        bool isLatest,
        out RuntimeRelease? release,
        out RuntimePackageAsset? asset)
    {
        release = null;
        asset = null;
        if (!TryGetString(item, "release_name", out string? releaseName)
            || !item.TryGetProperty("version_data", out JsonElement versionData)
            || !TryGetString(versionData, "openjdk_version", out string? openJdkVersion)
            || !RuntimeVersion.TryParse(openJdkVersion, out RuntimeVersion? version)
            || !item.TryGetProperty("binaries", out JsonElement binaries)
            || binaries.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement? matchingBinary = null;
        foreach (JsonElement candidate in binaries.EnumerateArray())
        {
            if (IsMatchingBinary(candidate))
            {
                matchingBinary = candidate;
                break;
            }
        }
        if (matchingBinary is null
            || !matchingBinary.Value.TryGetProperty("package", out JsonElement package)
            || !TryGetString(package, "name", out string? fileName)
            || !TryGetString(package, "link", out string? downloadLink)
            || !TryGetString(package, "checksum", out string? checksum)
            || !TryGetString(package, "signature_link", out string? signatureLink)
            || !Uri.TryCreate(downloadLink, UriKind.Absolute, out Uri? downloadUri)
            || !Uri.TryCreate(signatureLink, UriKind.Absolute, out Uri? signatureUri)
            || downloadUri.Scheme != Uri.UriSchemeHttps
            || signatureUri.Scheme != Uri.UriSchemeHttps
            || checksum.Length != 64
            || !checksum.All(Uri.IsHexDigit))
        {
            return false;
        }

        List<string> channels = ["ga"];
        if ((TryGetString(versionData, "optional", out string? optional)
                && optional.Contains("LTS", StringComparison.OrdinalIgnoreCase))
            || openJdkVersion.Contains("LTS", StringComparison.OrdinalIgnoreCase))
        {
            channels.Add("lts");
        }

        if (isLatest)
        {
            channels.Add("latest");
        }

        release = new RuntimeRelease(
            Id,
            releaseName!,
            Kind,
            version!,
            _architecture,
            "Eclipse Temurin",
            ParseDate(item),
            channels,
            false);
        Uri verificationSourceUri = requestUri;
        if (TryGetString(package, "checksum_link", out string? checksumLink)
            && Uri.TryCreate(checksumLink, UriKind.Absolute, out Uri? checksumUri)
            && checksumUri.Scheme == Uri.UriSchemeHttps)
        {
            verificationSourceUri = checksumUri;
        }

        asset = new RuntimePackageAsset(
            release,
            downloadUri,
            fileName!,
            checksum.ToLowerInvariant(),
            RuntimePackageFormat.Zip,
            releaseName,
            [
                new PackageVerification(
                    PackageVerificationKind.ProviderChecksum,
                    verificationSourceUri,
                    fileName!,
                    "SHA-256",
                    checksum.ToLowerInvariant()),
            ],
            [],
            PackageAuthenticityRequirement.DetachedPackageSignature,
            new PackageSignatureRequirement(
                PackageSignatureVerificationKind.OpenPgpDetached,
                signatureUri,
                SigningKeyUri,
                fileName!,
                SigningKeyFingerprint,
                PackageSignerTrust.ActiveAtTrustSnapshot));
        return true;
    }

    private bool IsMatchingBinary(JsonElement binary)
    {
        if (!TryGetString(binary, "architecture", out string? architecture)
            || !TryGetString(binary, "image_type", out string? imageType)
            || !TryGetString(binary, "os", out string? operatingSystem))
        {
            return false;
        }

        string expectedArchitecture = _architecture switch
        {
            RuntimeArchitecture.X64 => "x64",
            RuntimeArchitecture.X86 => "x32",
            RuntimeArchitecture.Arm64 => "aarch64",
            _ => string.Empty,
        };
        return architecture.Equals(expectedArchitecture, StringComparison.OrdinalIgnoreCase)
            && imageType.Equals("jdk", StringComparison.OrdinalIgnoreCase)
            && operatingSystem.Equals("windows", StringComparison.OrdinalIgnoreCase);
    }

    private static DateOnly? ParseDate(JsonElement item)
    {
        if (!TryGetString(item, "timestamp", out string? timestamp)
            || !DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset value))
        {
            return null;
        }

        return DateOnly.FromDateTime(value.UtcDateTime);
    }

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

    private void ValidateRelease(RuntimeRelease release)
    {
        if (!release.ProviderId.Equals(Id, StringComparison.Ordinal)
            || release.Kind != Kind
            || release.Architecture != _architecture
            || release.Version.Major != _featureVersion)
        {
            throw new ArgumentException("The release does not belong to this Temurin provider instance.", nameof(release));
        }
    }

    private static Uri EnsureTrailingSlash(Uri value)
    {
        if (!value.IsAbsoluteUri)
        {
            throw new ArgumentException("The provider base URI must be absolute.", nameof(value));
        }

        return value.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? value
            : new Uri(value.AbsoluteUri + "/");
    }
}
