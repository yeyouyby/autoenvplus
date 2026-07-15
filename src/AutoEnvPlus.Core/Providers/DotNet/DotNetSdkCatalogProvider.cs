using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Providers.DotNet;

public sealed class DotNetSdkCatalogProvider : IArchiveRuntimeProvider
{
    public const string ProviderName = "microsoft-dotnet-sdk";

    private static readonly Uri DefaultIndexUri = new(
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json");
    private readonly HttpClient _httpClient;
    private readonly RuntimeArchitecture _architecture;
    private readonly Uri _indexUri;
    private readonly ConcurrentDictionary<string, RuntimePackageAsset> _assets =
        new(StringComparer.Ordinal);

    public DotNetSdkCatalogProvider(
        HttpClient httpClient,
        RuntimeArchitecture architecture = RuntimeArchitecture.X64,
        Uri? indexUri = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _architecture = architecture is RuntimeArchitecture.X64
            or RuntimeArchitecture.X86
            or RuntimeArchitecture.Arm64
            ? architecture
            : throw new NotSupportedException(
                $".NET SDK ZIP packages do not support architecture '{architecture}' in AutoEnvPlus.");
        _indexUri = indexUri ?? DefaultIndexUri;
        EnsureHttps(_indexUri, "release index");
    }

    public string Id => ProviderName;

    public RuntimeKind Kind => RuntimeKind.DotNet;

    public async Task<IReadOnlyList<RuntimeRelease>> GetReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        using JsonDocument index = await ReadJsonAsync(_indexUri, cancellationToken)
            .ConfigureAwait(false);
        if (!index.RootElement.TryGetProperty("releases-index", out JsonElement channels)
            || channels.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The Microsoft .NET release index does not contain a releases-index array.");
        }

        ChannelDescriptor[] supportedChannels = channels
            .EnumerateArray()
            .Select(ParseChannel)
            .Where(channel => channel is not null)
            .Cast<ChannelDescriptor>()
            .OrderByDescending(channel => channel.Version)
            .Take(4)
            .ToArray();
        if (supportedChannels.Length == 0)
        {
            throw new InvalidDataException(
                "The Microsoft .NET release index does not contain an active stable SDK channel.");
        }

        IReadOnlyList<RuntimeRelease>[] channelReleases = await Task.WhenAll(
            supportedChannels.Select(channel => ReadChannelAsync(channel, cancellationToken)))
            .ConfigureAwait(false);
        return channelReleases
            .SelectMany(releases => releases)
            .GroupBy(
                release => $"{release.ProviderVersion}\0{release.Architecture}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(release => release.Version)
            .ThenBy(release => release.Architecture)
            .ToArray();
    }

    public async Task<RuntimePackageAsset> GetAssetAsync(
        RuntimeRelease release,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);
        if (_assets.TryGetValue(GetAssetKey(release), out RuntimePackageAsset? asset))
        {
            return asset;
        }

        await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
        return _assets.TryGetValue(GetAssetKey(release), out asset)
            ? asset
            : throw new InvalidDataException(
                $"The Microsoft .NET release metadata no longer contains SDK {release.ProviderVersion}.");
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
            "dotnet",
            "sdk",
            asset.Release.Version.ToString(),
            asset.Release.Architecture.ToString().ToLowerInvariant());
        return new ArchiveInstallPlan(
            asset,
            Path.GetFullPath(managedRoot),
            destination,
            "dotnet.exe",
            MaximumDownloadBytes: 1_610_612_736,
            MaximumArchiveEntries: 150_000,
            MaximumUncompressedBytes: 6_442_450_944);
    }

    private async Task<IReadOnlyList<RuntimeRelease>> ReadChannelAsync(
        ChannelDescriptor channel,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await ReadJsonAsync(
            channel.ReleasesUri,
            cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("releases", out JsonElement releases)
            || releases.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"The .NET {channel.Version} release metadata does not contain a releases array.");
        }

        Dictionary<string, RuntimeRelease> parsed = new(StringComparer.Ordinal);
        foreach (JsonElement releaseElement in releases.EnumerateArray())
        {
            DateOnly? releaseDate = ParseDate(releaseElement, "release-date");
            bool security = releaseElement.TryGetProperty("security", out JsonElement securityElement)
                && securityElement.ValueKind == JsonValueKind.True;
            foreach (JsonElement sdk in EnumerateSdks(releaseElement))
            {
                if (!TryParseSdk(
                        sdk,
                        channel,
                        releaseDate,
                        security,
                        out RuntimeRelease? release,
                        out RuntimePackageAsset? asset))
                {
                    continue;
                }

                parsed.TryAdd(release!.ProviderVersion, release);
                _assets[GetAssetKey(release)] = asset!;
            }
        }

        return parsed.Values
            .OrderByDescending(release => release.Version)
            .ToArray();
    }

    private bool TryParseSdk(
        JsonElement sdk,
        ChannelDescriptor channel,
        DateOnly? releaseDate,
        bool security,
        [NotNullWhen(true)] out RuntimeRelease? release,
        [NotNullWhen(true)] out RuntimePackageAsset? asset)
    {
        release = null;
        asset = null;
        if (!TryGetString(sdk, "version", out string? providerVersion)
            || !RuntimeVersion.TryParse(providerVersion, out RuntimeVersion? version)
            || version!.IsPrerelease
            || !sdk.TryGetProperty("files", out JsonElement files)
            || files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string expectedRid = _architecture switch
        {
            RuntimeArchitecture.X64 => "win-x64",
            RuntimeArchitecture.X86 => "win-x86",
            RuntimeArchitecture.Arm64 => "win-arm64",
            _ => throw new InvalidOperationException("Unexpected .NET SDK architecture."),
        };
        JsonElement selectedFile = default;
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (file.ValueKind == JsonValueKind.Object
                && TryGetString(file, "rid", out string? rid)
                && rid.Equals(expectedRid, StringComparison.OrdinalIgnoreCase)
                && TryGetString(file, "name", out string? name)
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                selectedFile = file;
                break;
            }
        }

        if (selectedFile.ValueKind != JsonValueKind.Object
            || !TryGetString(selectedFile, "name", out string? fileName)
            || !TryGetString(selectedFile, "url", out string? downloadValue)
            || !TryGetString(selectedFile, "hash", out string? packageHash)
            || !Uri.TryCreate(downloadValue, UriKind.Absolute, out Uri? downloadUri)
            || downloadUri.Scheme != Uri.UriSchemeHttps
            || !PackageHashAlgorithm.Sha512.IsValidHash(packageHash))
        {
            return false;
        }

        List<string> channels = ["sdk", channel.Version.ToString(), channel.ReleaseType];
        if (!string.IsNullOrWhiteSpace(channel.SupportPhase))
        {
            channels.Add(channel.SupportPhase);
        }

        if (providerVersion.Equals(channel.LatestSdk, StringComparison.Ordinal))
        {
            channels.Add("latest");
        }

        release = new RuntimeRelease(
            Id,
            providerVersion,
            Kind,
            version,
            _architecture,
            "Microsoft",
            releaseDate,
            channels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            security);
        asset = new RuntimePackageAsset(
            release,
            downloadUri,
            fileName,
            packageHash.ToLowerInvariant(),
            RuntimePackageFormat.Zip,
            null,
            [
                new PackageVerification(
                    PackageVerificationKind.ProviderChecksum,
                    channel.ReleasesUri,
                    fileName,
                    PackageHashAlgorithm.Sha512.DisplayName(),
                    packageHash.ToLowerInvariant()),
            ],
            [],
            PackageAuthenticityRequirement.ChecksumEvidence,
            HashAlgorithm: PackageHashAlgorithm.Sha512);
        return true;
    }

    private async Task<JsonDocument> ReadJsonAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 32 * 1024 * 1024)
        {
            throw new InvalidDataException(
                $"The Microsoft .NET release metadata is unexpectedly large: {uri}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 32 },
            cancellationToken).ConfigureAwait(false);
    }

    private static ChannelDescriptor? ParseChannel(JsonElement element)
    {
        if (!TryGetString(element, "channel-version", out string? channelVersion)
            || !Version.TryParse(channelVersion, out Version? version)
            || version.Build >= 0
            || !TryGetString(element, "releases.json", out string? releasesValue)
            || !Uri.TryCreate(releasesValue, UriKind.Absolute, out Uri? releasesUri)
            || releasesUri.Scheme != Uri.UriSchemeHttps
            || !TryGetString(element, "latest-sdk", out string? latestSdk))
        {
            return null;
        }

        string supportPhase = TryGetString(element, "support-phase", out string? phase)
            ? phase
            : string.Empty;
        if (!supportPhase.Equals("active", StringComparison.OrdinalIgnoreCase)
            && !supportPhase.Equals("maintenance", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string releaseType = TryGetString(element, "release-type", out string? type)
            ? type.ToLowerInvariant()
            : "stable";
        return new ChannelDescriptor(
            version,
            latestSdk,
            supportPhase.ToLowerInvariant(),
            releaseType,
            releasesUri);
    }

    private static IEnumerable<JsonElement> EnumerateSdks(JsonElement release)
    {
        if (release.TryGetProperty("sdks", out JsonElement sdks)
            && sdks.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement sdk in sdks.EnumerateArray())
            {
                yield return sdk;
            }
        }
        else if (release.TryGetProperty("sdk", out JsonElement sdk)
                 && sdk.ValueKind == JsonValueKind.Object)
        {
            yield return sdk;
        }
    }

    private static DateOnly? ParseDate(JsonElement element, string propertyName)
    {
        return TryGetString(element, propertyName, out string? value)
            && DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly result)
            ? result
            : null;
    }

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString());
    }

    private void ValidateRelease(RuntimeRelease release)
    {
        if (!release.ProviderId.Equals(Id, StringComparison.Ordinal)
            || release.Kind != Kind
            || release.Architecture != _architecture)
        {
            throw new ArgumentException(
                "The release does not belong to this .NET SDK provider instance.",
                nameof(release));
        }
    }

    private static string GetAssetKey(RuntimeRelease release) =>
        $"{release.ProviderVersion}\0{release.Architecture}";

    private static void EnsureHttps(Uri uri, string description)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"The .NET SDK {description} must be an absolute HTTPS URI.",
                nameof(uri));
        }
    }

    private sealed record ChannelDescriptor(
        Version Version,
        string LatestSdk,
        string SupportPhase,
        string ReleaseType,
        Uri ReleasesUri);
}
