using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Providers.NodeJs;

public sealed partial class NodeJsCatalogProvider : IArchiveRuntimeProvider
{
    public const string ProviderName = "nodejs-official";

    private static readonly Uri DefaultBaseUri = new("https://nodejs.org/dist/");
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly INodeReleaseSignatureVerifier _signatureVerifier;

    public NodeJsCatalogProvider(
        HttpClient httpClient,
        Uri? baseUri = null,
        INodeReleaseSignatureVerifier? signatureVerifier = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = EnsureTrailingSlash(baseUri ?? DefaultBaseUri);
        _signatureVerifier = signatureVerifier ?? new NodeReleaseSignatureVerifier(_httpClient);
    }

    public string Id => ProviderName;

    public RuntimeKind Kind => RuntimeKind.NodeJs;

    public async Task<IReadOnlyList<RuntimeRelease>> GetReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            new Uri(_baseUri, "index.json"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Node.js release index is not a JSON array.");
        }

        List<RuntimeRelease> releases = [];
        HashSet<RuntimeArchitecture> latestTagged = [];

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("version", out JsonElement versionElement)
                || versionElement.GetString() is not string providerVersion
                || !RuntimeVersion.TryParse(providerVersion, out RuntimeVersion? version)
                || version!.IsPrerelease)
            {
                continue;
            }

            DateOnly? releaseDate = ParseDate(item);
            bool isSecurityRelease = item.TryGetProperty("security", out JsonElement securityElement)
                && securityElement.ValueKind == JsonValueKind.True;
            IReadOnlyList<string> baseChannels = ParseChannels(item);

            foreach (RuntimeArchitecture architecture in ParseArchitectures(item))
            {
                List<string> channels = [.. baseChannels];
                if (latestTagged.Add(architecture))
                {
                    channels.Add("latest");
                }

                releases.Add(new RuntimeRelease(
                    Id,
                    providerVersion,
                    Kind,
                    version!,
                    architecture,
                    "Node.js",
                    releaseDate,
                    channels,
                    isSecurityRelease));
            }
        }

        return releases
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

        string architecture = release.Architecture switch
        {
            RuntimeArchitecture.X64 => "x64",
            RuntimeArchitecture.X86 => "x86",
            RuntimeArchitecture.Arm64 => "arm64",
            _ => throw new NotSupportedException($"Node.js does not provide a ZIP asset for {release.Architecture}."),
        };

        string fileName = $"node-{release.ProviderVersion}-win-{architecture}.zip";
        Uri releaseBaseUri = new(_baseUri, release.ProviderVersion.TrimEnd('/') + "/");
        Uri signedChecksumsUri = new(releaseBaseUri, "SHASUMS256.txt.asc");
        VerifiedNodeReleaseChecksums verifiedChecksums = await _signatureVerifier.GetVerifiedChecksumsAsync(
            signedChecksumsUri,
            release.ReleaseDate,
            cancellationToken).ConfigureAwait(false);

        string? sha256 = ParseChecksum(verifiedChecksums.Content, fileName);
        if (sha256 is null)
        {
            throw new InvalidDataException(
                $"The signed SHASUMS256.txt does not contain the expected asset '{fileName}'.");
        }

        return new RuntimePackageAsset(
            release,
            new Uri(releaseBaseUri, fileName),
            fileName,
            sha256,
            RuntimePackageFormat.Zip,
            Path.GetFileNameWithoutExtension(fileName),
            [
                new PackageVerification(
                    PackageVerificationKind.ProviderChecksum,
                    signedChecksumsUri,
                    fileName,
                    "SHA-256",
                    sha256),
            ],
            [verifiedChecksums.Verification],
            PackageAuthenticityRequirement.SignedChecksumManifest);
    }

    public ArchiveInstallPlan CreateInstallPlan(
        RuntimePackageAsset asset,
        string managedRoot)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ValidateRelease(asset.Release);

        string architecture = asset.Release.Architecture.ToString().ToLowerInvariant();
        string destination = Path.Combine(
            Path.GetFullPath(managedRoot),
            "runtimes",
            "node",
            asset.Release.Version.ToString(),
            architecture);

        return new ArchiveInstallPlan(
            asset,
            Path.GetFullPath(managedRoot),
            destination,
            "node.exe");
    }

    private static IReadOnlyList<RuntimeArchitecture> ParseArchitectures(JsonElement item)
    {
        if (!item.TryGetProperty("files", out JsonElement files)
            || files.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        HashSet<string> values = files
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<RuntimeArchitecture> architectures = [];

        if (values.Contains("win-x64-zip"))
        {
            architectures.Add(RuntimeArchitecture.X64);
        }

        if (values.Contains("win-x86-zip"))
        {
            architectures.Add(RuntimeArchitecture.X86);
        }

        if (values.Contains("win-arm64-zip"))
        {
            architectures.Add(RuntimeArchitecture.Arm64);
        }

        return architectures;
    }

    private static IReadOnlyList<string> ParseChannels(JsonElement item)
    {
        if (item.TryGetProperty("lts", out JsonElement ltsElement)
            && ltsElement.ValueKind == JsonValueKind.String
            && ltsElement.GetString() is string codeName
            && !string.IsNullOrWhiteSpace(codeName))
        {
            return ["lts", codeName.Trim().ToLowerInvariant()];
        }

        return ["current"];
    }

    private static DateOnly? ParseDate(JsonElement item)
    {
        if (!item.TryGetProperty("date", out JsonElement dateElement)
            || dateElement.GetString() is not string date)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            date,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateOnly result)
            ? result
            : null;
    }

    private static string? ParseChecksum(string checksums, string fileName)
    {
        foreach (string line in checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = ChecksumLinePattern().Match(line.Trim());
            if (match.Success
                && match.Groups["file"].Value.Equals(fileName, StringComparison.Ordinal))
            {
                return match.Groups["hash"].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    private void ValidateRelease(RuntimeRelease release)
    {
        if (!release.ProviderId.Equals(Id, StringComparison.Ordinal)
            || release.Kind != Kind)
        {
            throw new ArgumentException("The release does not belong to this provider.", nameof(release));
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

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<file>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLinePattern();
}
